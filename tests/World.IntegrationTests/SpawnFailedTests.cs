using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ELifeRPG.World.IntegrationTests.AckResults;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 5's negative-ack path
/// (<c>SpawnFailedCommand</c>, backing <c>POST /api/inventory/instances/{id}/spawn-failed</c>) and the
/// staff undeliverable queue (<c>UndeliverableInstancesQuery</c>, backing
/// <c>GET /api/inventory/undeliverable</c>). Neither of these mutates <c>PendingSpawn</c> or
/// <c>DeliveryAttempts</c> — both are owned entirely by the pending-delivery read (task 4's
/// <c>PendingDeliveriesHandler</c>); "undeliverable" is a derived read over that same counter, never a
/// stored flag of its own — see <c>IItemInstanceRepository.FindUndeliverableAsync</c>'s doc comment.
///
/// Outcomes are asserted with <c>is</c> pattern matching, not <c>Assert.IsType</c>: a <c>union</c>
/// case's runtime type is the declaring union type itself (the case is a compiler-recognized pattern,
/// not a distinct CLR subtype), so a strict <c>GetType() == typeof(TCase)</c> check never matches.
/// </summary>
public sealed class SpawnFailedTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static async Task<CharacterId> CreateCharacterAsync(ServiceProvider provider, string name)
    {
        await using var scope = provider.CreateAsyncScope();
        var accountId = (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateCharacterCommand(accountId, name));
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.CharacterId;
    }

    private static async Task<ItemInstanceId> GrantOneAsync(ServiceProvider provider, CharacterId owner)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var itemResult = await mediator.Send(new CreateItemCommand("Test Item", $"Test_{Guid.NewGuid():N}"));
        if (itemResult is not CreateItemResult.Created itemCreated)
        {
            throw new InvalidOperationException($"Expected Created, got {itemResult}");
        }

        var grantResult = await mediator.Send(new GrantItemsCommand(
            itemCreated.ItemId, 1, owner, ItemOrigin.ShopPurchase, new OriginRef("Shops", Guid.NewGuid().ToString())));
        if (grantResult is not GrantItemsResult.Granted granted)
        {
            throw new InvalidOperationException($"Expected Granted, got {grantResult}");
        }

        return granted.Instances[0].InstanceId;
    }

    [Fact]
    public async Task SpawnFailed_WithInventoryFull_LeavesTheInstancePendingForRetry()
    {
        var owner = await CreateCharacterAsync(_provider, "Spawn Failed Character");
        var instanceId = await GrantOneAsync(_provider, owner);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(new SpawnFailedCommand(serverId, instanceId, SpawnFailureReason.InventoryFull));

        Assert.True(result is SpawnFailedResult.StillPending, $"Expected StillPending, got {result}");

        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.PendingSpawn);
        Assert.Equal(0, reloaded.DeliveryAttempts); // spawn-failed itself never touches this counter
    }

    [Fact]
    public async Task SpawnFailed_ForAnUnknownInstanceId_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(
            new SpawnFailedCommand(serverId, new ItemInstanceId(Guid.NewGuid()), SpawnFailureReason.PrefabMissing));

        Assert.True(result is SpawnFailedResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task PendingInstance_ServedBeyondTheDeliveryCap_StopsBeingOfferedAndIsListedUndeliverable()
    {
        var owner = await CreateCharacterAsync(_provider, "Delivery Cap Character");
        var instanceId = await GrantOneAsync(_provider, owner);

        int maxDeliveryAttempts;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            maxDeliveryAttempts = (await mediator.Send(new WorldSettingsQuery())).MaxDeliveryAttempts;
        }

        // Serve the row via the pending read exactly up to the cap — each call increments
        // DeliveryAttempts (task 4), which is what "undeliverable" is derived from.
        for (var attempt = 0; attempt < maxDeliveryAttempts; attempt++)
        {
            await using var scope = _provider.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var served = await mediator.Send(new PendingDeliveriesQuery(owner, null));
            Assert.Contains(served, x => x.Id == instanceId);
        }

        await using var finalScope = _provider.CreateAsyncScope();
        var finalMediator = finalScope.ServiceProvider.GetRequiredService<IMediator>();

        var pending = await finalMediator.Send(new PendingDeliveriesQuery(owner, null));
        Assert.DoesNotContain(pending, x => x.Id == instanceId);

        var undeliverable = await finalMediator.Send(new UndeliverableInstancesQuery());
        Assert.Contains(undeliverable, x => x.Id == instanceId);

        // A spawn-failed report against the now-capped row reflects that state back, purely informationally.
        var currentGameServer = finalScope.ServiceProvider.GetRequiredService<ICurrentGameServer>();
        var serverId = await currentGameServer.GetIdAsync(CancellationToken.None);
        var negativeAck = await finalMediator.Send(new SpawnFailedCommand(serverId, instanceId, SpawnFailureReason.InventoryFull));
        Assert.True(negativeAck is SpawnFailedResult.Undeliverable, $"Expected Undeliverable, got {negativeAck}");
    }

    /// <summary>Review round 1, B-4: split from NotFound — same reasoning as the ack path's AckOutcome.WrongServer.</summary>
    [Fact]
    public async Task SpawnFailed_ForACharacterOnAnotherServer_ReturnsWrongServer()
    {
        var homeServerProvider = TestServices.BuildProvider("spawn-failed-home");
        var awayServerProvider = TestServices.BuildProvider("spawn-failed-away");
        await using var _1 = homeServerProvider;
        await using var _2 = awayServerProvider;

        var owner = await CreateCharacterAsync(homeServerProvider, "Home Character");
        var instanceId = await GrantOneAsync(homeServerProvider, owner);

        await using var awayScope = awayServerProvider.CreateAsyncScope();
        var mediator = awayScope.ServiceProvider.GetRequiredService<IMediator>();
        var awayServerId = await awayScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(new SpawnFailedCommand(awayServerId, instanceId, SpawnFailureReason.InventoryFull));

        Assert.True(result is SpawnFailedResult.WrongServer, $"Expected WrongServer, got {result}");

        await using var readScope = homeServerProvider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.LastSpawnFailureReason); // rejected, so nothing was recorded either
    }

    /// <summary>
    /// Review round 1, B-3: a mod reporting InventoryFull and a mod silently dropping the item used to
    /// leave the backend in byte-identical states. The reason, its timestamp, and a count must now be
    /// persisted so staff can tell them apart on the undeliverable queue.
    /// </summary>
    [Fact]
    public async Task SpawnFailed_PersistsTheLastFailureReasonItsTimestampAndACount()
    {
        var owner = await CreateCharacterAsync(_provider, "Failure Reason Character");
        var instanceId = await GrantOneAsync(_provider, owner);

        await using (var scope1 = _provider.CreateAsyncScope())
        {
            var mediator1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
            var serverId1 = await scope1.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
            await mediator1.Send(new SpawnFailedCommand(serverId1, instanceId, SpawnFailureReason.InventoryFull));
        }

        DateTimeOffset firstFailureAt;
        await using (var readScope1 = _provider.CreateAsyncScope())
        {
            var repository1 = readScope1.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var afterFirst = await repository1.FindByIdAsync(instanceId, CancellationToken.None);
            Assert.NotNull(afterFirst);
            Assert.Equal(SpawnFailureReason.InventoryFull, afterFirst.LastSpawnFailureReason);
            Assert.NotNull(afterFirst.LastSpawnFailureAt);
            Assert.Equal(1, afterFirst.SpawnFailureCount);
            firstFailureAt = afterFirst.LastSpawnFailureAt!.Value;
        }

        await using (var scope2 = _provider.CreateAsyncScope())
        {
            var mediator2 = scope2.ServiceProvider.GetRequiredService<IMediator>();
            var serverId2 = await scope2.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
            await mediator2.Send(new SpawnFailedCommand(serverId2, instanceId, SpawnFailureReason.PrefabMissing));
        }

        await using var readScope2 = _provider.CreateAsyncScope();
        var repository2 = readScope2.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var afterSecond = await repository2.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(afterSecond);
        Assert.Equal(SpawnFailureReason.PrefabMissing, afterSecond.LastSpawnFailureReason); // most recent wins
        Assert.True(afterSecond.LastSpawnFailureAt >= firstFailureAt);
        Assert.Equal(2, afterSecond.SpawnFailureCount);
    }

    /// <summary>
    /// Review round 2, item (i): once a row is acked (no longer PendingSpawn), a spawn-failed report
    /// against it must not silently record anything or claim StillPending — that would be false, and
    /// before this guard an authenticated gameserver could drive SpawnFailureCount/UpdatedAt on any
    /// known instance id, including an already-delivered one, without bound.
    /// </summary>
    [Fact]
    public async Task SpawnFailed_ForAnAlreadyAckedInstance_ReturnsNotPendingAndRecordsNothing()
    {
        var owner = await CreateCharacterAsync(_provider, "Already Acked Character");
        var instanceId = await GrantOneAsync(_provider, owner);

        DateTimeOffset updatedAtAfterAck;
        await using (var ackScope = _provider.CreateAsyncScope())
        {
            var mediator = ackScope.ServiceProvider.GetRequiredService<IMediator>();
            var serverId = await ackScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
            var ackResults = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(instanceId, [])])));
            if (Assert.Single(ackResults).Outcome is not AckOutcome.Cleared)
            {
                throw new InvalidOperationException("Expected Cleared.");
            }
        }

        await using (var readScope = _provider.CreateAsyncScope())
        {
            var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var acked = await repository.FindByIdAsync(instanceId, CancellationToken.None);
            Assert.NotNull(acked);
            Assert.False(acked.PendingSpawn);
            updatedAtAfterAck = acked.UpdatedAt;
        }

        await using var spawnFailedScope = _provider.CreateAsyncScope();
        var spawnFailedMediator = spawnFailedScope.ServiceProvider.GetRequiredService<IMediator>();
        var spawnFailedServerId = await spawnFailedScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await spawnFailedMediator.Send(new SpawnFailedCommand(spawnFailedServerId, instanceId, SpawnFailureReason.InventoryFull));
        Assert.True(result is SpawnFailedResult.NotPending, $"Expected NotPending, got {result}");

        await using var finalReadScope = _provider.CreateAsyncScope();
        var finalRepository = finalReadScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await finalRepository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.LastSpawnFailureReason);
        Assert.Null(reloaded.LastSpawnFailureAt);
        Assert.Equal(0, reloaded.SpawnFailureCount);
        Assert.Equal(updatedAtAfterAck, reloaded.UpdatedAt); // untouched — nothing was written
    }
}
