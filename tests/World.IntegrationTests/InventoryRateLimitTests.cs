using System.Security.Claims;
using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Domain;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Task 6's two testable pieces of the rate-limited write path: the partition key
/// (<c>WorldModule.RateLimitPartitionKey</c>) and the promise <c>GET /api/inventory/limits</c> makes
/// about the buckets. Same seam and same reasoning as <see cref="SnapshotEndpointContractTests"/> — see
/// that file's doc comment, and <c>World.Api</c>'s <c>AssemblyInfo.cs</c>: these are pure functions with
/// no HTTP, DI or database dependence, and the only thing that would have stopped them being asserted
/// on directly is an access modifier.
///
/// What is deliberately NOT tested here is that the middleware actually enforces the bucket. That is
/// ASP.NET Core's own <c>TokenBucketRateLimiter</c>, exercised through a live host, and asserting it
/// would need the <c>WebApplicationFactory</c> harness this phase has consistently declined to build —
/// it was instead verified by hand against a running stack, and the walkthrough that does it is written
/// down in <c>docs/bridge.md</c>. What IS worth pinning here is everything a future change could get
/// wrong silently: a bucket that queues instead of rejecting, and a published limit that stops matching
/// the enforced one.
///
/// No <see cref="TestServices"/> provider and no <c>IAsyncLifetime</c>: nothing here needs a scope.
/// </summary>
public sealed class InventoryRateLimitTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void RateLimitPartitionKey_ForATokenCarryingAClientId_IsThatClientId()
    {
        var key = WorldModule.RateLimitPartitionKey(PrincipalWith(new Claim("client_id", "gameserver-everon")));

        Assert.Equal("gameserver-everon", key);
    }

    /// <summary>
    /// One Keycloak client per gameserver instance is the credential model (ARCHITECTURE.md §4.2), so
    /// this is what makes a bucket per-gameserver: two servers flooding independently must not consume
    /// each other's allowance.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKey_ForTwoDifferentClients_AreDifferentPartitions()
    {
        var everon = WorldModule.RateLimitPartitionKey(PrincipalWith(new Claim("client_id", "gameserver-everon")));
        var arland = WorldModule.RateLimitPartitionKey(PrincipalWith(new Claim("client_id", "gameserver-arland")));

        Assert.NotEqual(everon, arland);
    }

    /// <summary>
    /// The claimless case must fall into a shared bucket rather than throw. It is unreachable through
    /// the pipeline as ordered — authorization runs before the rate limiter and rejects an
    /// unauthenticated request first — but a partitioner throws inside middleware, not inside a
    /// handler, so an exception here would take every rate-limited endpoint down rather than fail one
    /// request. See <c>WorldModule.RateLimitPartitionKey</c>'s own doc comment for the contrast with
    /// <c>HttpContextCurrentGameServerClientId</c>, which throws on the same missing claim on purpose.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKey_ForAPrincipalWithNoClientId_IsTheSharedUnattributedPartition()
    {
        var key = WorldModule.RateLimitPartitionKey(PrincipalWith(new Claim("scope", "gameserver:inventory:write")));

        Assert.Equal(WorldModule.UnattributedRateLimitPartition, key);
    }

    /// <summary>An empty claim value is as unusable as an absent one, and must not become its own partition.</summary>
    [Fact]
    public void RateLimitPartitionKey_ForAnEmptyClientIdClaim_IsTheSharedUnattributedPartition()
    {
        var key = WorldModule.RateLimitPartitionKey(PrincipalWith(new Claim("client_id", string.Empty)));

        Assert.Equal(WorldModule.UnattributedRateLimitPartition, key);
    }

    /// <summary>
    /// Nothing queues on either bucket. A queued request is a held connection the caller cannot see,
    /// and the caller here is a buffering client that would rather be told "not now, in N seconds" at
    /// once and keep its own durable copy. A non-zero <c>QueueLimit</c> would turn an instant,
    /// actionable 429 into a slow 200 and silently break the Retry-After contract docs/bridge.md makes.
    /// </summary>
    [Fact]
    public void Buckets_ForBothWritePolicies_QueueNothing()
    {
        Assert.Equal(0, InventoryRateLimits.Snapshots().QueueLimit);
        Assert.Equal(0, InventoryRateLimits.UnknownPrefabReports().QueueLimit);
    }

    /// <summary>
    /// The unknown-prefab report is the endpoint whose own description has asked for an "aggressive"
    /// policy since task 5, and aggressive is a relative claim: it only means anything against the
    /// snapshot path it sits beside. Pinning the relation rather than the numbers lets a deployment
    /// retune either bucket without touching this test, while stopping the one change that would
    /// quietly undo task 5's request — raising the telemetry endpoint to the write path's rate.
    /// </summary>
    [Fact]
    public void Buckets_ForTheUnknownPrefabReport_AreTighterThanTheSnapshotPath()
    {
        var snapshots = InventoryRateLimits.Snapshots();
        var unknownPrefabs = InventoryRateLimits.UnknownPrefabReports();

        Assert.True(unknownPrefabs.TokenLimit < snapshots.TokenLimit);
        Assert.True(
            InventoryRateLimits.RequestsPerMinute(unknownPrefabs) < InventoryRateLimits.RequestsPerMinute(snapshots));
    }

    /// <summary>
    /// <c>GET /api/inventory/limits</c> exists so the Bridge hardcodes nothing, which is only worth
    /// anything if what it publishes is what the host enforces. The DTO derives both figures from the
    /// same bucket objects <c>WorldModule</c> hands the rate limiter, so this asserts the derivation
    /// rather than a literal — a retuned bucket moves both sides together, and a hand-edited published
    /// number fails here.
    /// </summary>
    [Fact]
    public void Create_ForTheRateLimitBuckets_PublishesWhatIsActuallyEnforced()
    {
        var limits = WorldLimitsDto.Create(new WorldSettings());
        var snapshots = InventoryRateLimits.Snapshots();
        var unknownPrefabs = InventoryRateLimits.UnknownPrefabReports();

        Assert.Equal(snapshots.TokenLimit, limits.SnapshotRequestBurst);
        Assert.Equal(InventoryRateLimits.RequestsPerMinute(snapshots), limits.SnapshotRequestsPerMinute);
        Assert.Equal(unknownPrefabs.TokenLimit, limits.UnknownPrefabRequestBurst);
        Assert.Equal(InventoryRateLimits.RequestsPerMinute(unknownPrefabs), limits.UnknownPrefabRequestsPerMinute);
    }

    /// <summary>
    /// The derivation itself, on the case that is not a plain multiplication: the unknown-prefab bucket
    /// replenishes one token every 30 seconds, which is 2/minute, not 1. Getting this backwards would
    /// publish a rate the Bridge then sizes its flush interval against.
    /// </summary>
    [Fact]
    public void RequestsPerMinute_ForASubMinuteReplenishmentPeriod_ScalesToTheMinute()
    {
        Assert.Equal(600, InventoryRateLimits.RequestsPerMinute(InventoryRateLimits.Snapshots()));
        Assert.Equal(2, InventoryRateLimits.RequestsPerMinute(InventoryRateLimits.UnknownPrefabReports()));
    }
}
