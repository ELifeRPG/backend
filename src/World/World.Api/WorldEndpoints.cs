using System.Threading.RateLimiting;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Api.Common;
using ELifeRPG.World.Api.Gathering;
using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Gathering;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The World module's host wiring. Task 3 adds the limits endpoint; tasks 4-5 add the rest of the
/// delivery and acknowledgement surface. The scopes are declared next to their policies, matching how
/// every other module (see <c>ItemModule</c>) does it.
/// </summary>
public static partial class WorldModule
{
    public const string InventoryReadScope = "gameserver:inventory:read";
    public const string InventoryWriteScope = "gameserver:inventory:write";
    public const string InventoryManageScope = "inventory:manage";

    private const string InventoryReadPolicy = "Inventory.Read";
    private const string InventoryWritePolicy = "Inventory.Write";
    private const string InventoryManagePolicy = "Inventory.Manage";

    /// <summary>
    /// Either the gameserver's read scope or staff's manage scope satisfies this. It exists for exactly
    /// one endpoint — <c>GET /api/inventory/limits</c> — because that resource is read by two audiences
    /// with different scopes: the Bridge, which reads its caps at boot, and staff, who need to see the
    /// current values of the knobs they are about to PATCH. Gating the read on the gameserver scope
    /// alone left an admin surface a staff token could write but not read, so the only way to see the
    /// limits was to mutate them and inspect the response. The write half stays <see cref="InventoryManagePolicy"/>.
    /// </summary>
    private const string InventoryLimitsReadPolicy = "Inventory.Limits.Read";

    /// <summary>Task 6's rate-limiting policy for <c>POST /api/inventory/snapshots</c> — see <see cref="InventoryRateLimits.Snapshots"/>.</summary>
    private const string SnapshotRateLimitPolicy = "Inventory.Snapshots.RateLimit";

    /// <summary>Task 6's rate-limiting policy for <c>POST /api/inventory/unknown-prefabs</c> — see <see cref="InventoryRateLimits.UnknownPrefabReports"/>.</summary>
    private const string UnknownPrefabRateLimitPolicy = "Inventory.UnknownPrefabs.RateLimit";

    /// <summary>
    /// The partition every request with no usable <c>client_id</c> claim shares. Unreachable in
    /// practice — <c>UseRateLimiter()</c> runs after <c>UseAuthorization()</c>, and both rate-limited
    /// endpoints require a scope, so an unauthenticated request is already a 401 before the partitioner
    /// is consulted — but see <see cref="RateLimitPartitionKey"/> for why it exists anyway.
    /// </summary>
    internal const string UnattributedRateLimitPartition = "(no client_id)";

    public static IServiceCollection AddWorldModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWorldInfrastructure(configuration);

        // Resolves the calling Bridge's own gameserver identity for the ack/spawn-failed server guard
        // — copied per module by design, see ICurrentGameServer's own doc comment.
        services.AddScoped<ICurrentGameServerClientId, HttpContextCurrentGameServerClientId>();
        services.AddScoped<ICurrentGameServer, RegistryCurrentGameServer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(InventoryReadPolicy, policy => policy.RequireAssertion(context => HasScope(context, InventoryReadScope)))
            .AddPolicy(InventoryWritePolicy, policy => policy.RequireAssertion(context => HasScope(context, InventoryWriteScope)))
            .AddPolicy(InventoryManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, InventoryManageScope)))
            .AddPolicy(
                InventoryLimitsReadPolicy,
                policy => policy.RequireAssertion(context =>
                    HasScope(context, InventoryReadScope) || HasScope(context, InventoryManageScope)));

        // Task 6: this module's own rate-limiting policies, registered here for exactly the reason its
        // authorization policies are — a module owns the policies its endpoints name, and the host owns
        // only the pipeline and the shared shape of a rejection (see Program.cs). AddRateLimiter is a
        // plain services.Configure<RateLimiterOptions> under the hood, so the host's call and this one
        // compose rather than one replacing the other.
        //
        // Partitioned on client_id, which for this deployment means "per gameserver": one Keycloak
        // client per gameserver instance is already the credential model (ARCHITECTURE.md §4.2), so
        // partitioning on it gives each server its own bucket and makes one misbehaving server's flood
        // its own problem rather than the hive's. Partitioning on the character or the scope instead
        // would be finer-grained but unbounded — a compromised or buggy client could mint partitions
        // faster than they expire simply by naming new ids — whereas the set of client_id values is
        // bounded by the set of registered clients, which is exactly the property a partition key needs.
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(SnapshotRateLimitPolicy, httpContext => RateLimitPartition.GetTokenBucketLimiter(
                RateLimitPartitionKey(httpContext.User),
                _ => InventoryRateLimits.Snapshots()));

            options.AddPolicy(UnknownPrefabRateLimitPolicy, httpContext => RateLimitPartition.GetTokenBucketLimiter(
                RateLimitPartitionKey(httpContext.User),
                _ => InventoryRateLimits.UnknownPrefabReports()));
        });

        return services;
    }

    /// <summary>
    /// Which bucket a request draws from: the calling client's own <c>client_id</c> claim, the same
    /// claim <c>HttpContextCurrentGameServerClientId</c> resolves the gameserver identity from.
    ///
    /// Unlike that class, this one does <b>not</b> throw when the claim is missing. The difference is
    /// where each runs: that one runs inside an endpoint handler, where an exception becomes one
    /// request's 500, and a missing claim there really is a misconfiguration worth failing loudly on.
    /// This one runs inside the rate-limiting middleware's partitioner, where an exception is not
    /// attributable to anything a caller did wrong and takes the middleware — and therefore every
    /// rate-limited endpoint — down with it. So the claimless case falls into one shared bucket
    /// instead: it cannot be reached through the pipeline as ordered (authorization runs first and
    /// rejects it), and if some later reordering ever did make it reachable, sharing a single bucket is
    /// the conservative outcome rather than the permissive one. A real client_id colliding with the
    /// sentinel would only mean sharing that bucket, never escaping one.
    /// </summary>
    internal static string RateLimitPartitionKey(System.Security.Claims.ClaimsPrincipal user)
        => user.FindFirst("client_id")?.Value is { Length: > 0 } clientId ? clientId : UnattributedRateLimitPartition;

    public static WebApplication MapWorldModule(this WebApplication app)
    {
        var group = app.MapGroup("api/inventory").WithTags("Inventory");

        group.MapGet("limits", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var settings = await mediator.Send(new WorldSettingsQuery(), cancellationToken);
                return Results.Ok(WorldLimitsDto.Create(settings));
            })
            .RequireAuthorization(InventoryLimitsReadPolicy)
            .Produces<WorldLimitsDto>()
            .WithName("GetInventoryLimits")
            .WithDescription(
                "Reports the operationally tunable grant/delivery settings alongside the structural "
                + "domain constants (container depth, attribute limits), so the Bridge hardcodes nothing.");

        // The write half of the same resource. Phase 2 settled three reconcile-guard thresholds on the
        // grounds that they are deployment knobs "retunable against real data" while there was no write
        // path of any kind — no UpsertAsync, no command, no endpoint, and a settings table holding zero
        // rows. This is that path, shaped after the HiveSettings precedent it was justified against:
        // PATCH, partial (omitted fields unchanged), staff-gated.
        //
        // On PATCH-ing `limits` rather than a separate `settings` resource: the Bridge is told to read
        // its caps from exactly one URL, and staff tuning them at that same URL is what keeps the two
        // from drifting into separate mental models. The request body carries only the tunable subset —
        // see UpdateWorldLimitsRequestDto for why the structural constants and the rate-limit figures
        // are not settable — and the response is the full limits document, so an operator sees the
        // composed result the Bridge will read rather than only the half they sent.
        group.MapPatch("limits", async (
                [FromBody] UpdateWorldLimitsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var updated = await mediator.Send(request.ToCommand(), cancellationToken);
                    return Results.Ok(WorldLimitsDto.Create(updated));
                }
                catch (WorldSettingOutOfRangeException exception)
                {
                    // Catch-and-map, ARCHITECTURE.md §9e: a domain guard for an outcome the caller can
                    // reasonably trigger becomes a problem document, never a 500. Deliberately unlike
                    // the HiveSettings precedent, which lets an ArgumentOutOfRangeException escape —
                    // this module's contract is that every rejection on its write surface carries the
                    // `retryable` flag (docs/bridge.md), and a 500 carries neither that nor the
                    // problem+json shape a client parses. The message names the knob and its allowed
                    // range, which is what an operator needs to correct the request.
                    return Results.Problem(
                        title: $"setting_out_of_range: {exception.Message}",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: NotRetryableExtensions());
                }
            })
            .RequireAuthorization(InventoryManagePolicy)
            .Produces<WorldLimitsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("UpdateInventoryLimits")
            .WithDescription(
                "Partially updates this deployment's tunable inventory settings; omitted fields are "
                + "left unchanged. Only the tunable half of GET /api/inventory/limits is writable — the "
                + "structural domain constants and the rate-limit figures are not. Every value is "
                + "range-checked rather than clamped: an out-of-range knob is `setting_out_of_range` "
                + "(400, not retryable) naming the knob and its allowed range, and nothing is written. "
                + "Requires the staff `inventory:manage` scope, like the other staff-facing reads here.");

        group.MapGet("characters/{characterId:guid}/items", async (
                Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var items = await mediator.Send(new CarriedInventoryQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(items.Select(ItemInstanceDto.Create).ToList());
            })
            .RequireAuthorization(InventoryReadPolicy)
            .Produces<List<ItemInstanceDto>>()
            .WithName("GetCarriedInventory")
            .WithDescription(
                "What this character actually HOLDS: the flat set of live instances rooted at it, with "
                + "soft-deleted, staff-removed, expired and still-pending (not yet spawned/acked) rows "
                + "excluded. A row owed but never delivered belongs only on GET .../pending, never here — "
                + "spawn everything this endpoint returns, and separately spawn+ack everything "
                + "GET .../pending returns; the two lists never overlap. Deliberately unpaginated — "
                + "Reforger's own volume system bounds what a character can carry, so the payload is "
                + "self-limiting. Each row carries its revision so the mod can seed its counters; the "
                + "client rebuilds the container tree from containerInstanceId.");

        group.MapGet("characters/{characterId:guid}/pending", async (
                Guid characterId,
                int? limit,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var items = await mediator.Send(new PendingDeliveriesQuery(new CharacterId(characterId), limit), cancellationToken);
                return Results.Ok(items.Select(ItemInstanceDto.Create).ToList());
            })
            .RequireAuthorization(InventoryReadPolicy)
            .Produces<List<ItemInstanceDto>>()
            .WithName("GetPendingDeliveries")
            .WithDescription(
                "What this character is still OWED: instances not yet spawned, oldest-first and bounded "
                + "by WorldSettings.MaxPendingPageSize (limit is clamped down to it, never up). Excludes "
                + "rows already at MaxDeliveryAttempts. Disjoint from GET .../items — a row surfaced here "
                + "has never been spawned, so it will not also appear there; do not spawn it twice. Not a "
                + "side-effect-free peek: every row returned here has its DeliveryAttempts incremented, "
                + "since being served in this payload is what an attempt means. The mod spawns what it "
                + "can, acks, and asks again.");

        group.MapPost("acks", async (
                [FromBody] AcknowledgeSpawnsRequestDto request,
                IMediator mediator,
                ICurrentGameServer currentGameServer,
                CancellationToken cancellationToken) =>
            {
                var serverId = await currentGameServer.GetIdAsync(cancellationToken);
                var acks = request.Acks
                    .Select(ack => new InstanceAckRequest(
                        new ItemInstanceId(ack.InstanceId),
                        ack.Children.Select(c => new AckChildRequest(new ItemId(c.ItemId), c.Slot)).ToList()))
                    .ToList();

                var result = await mediator.Send(new AcknowledgeSpawnsCommand(serverId, acks), cancellationToken);

                return result switch
                {
                    AcknowledgeSpawnsResult.Acknowledged acknowledged =>
                        Results.Ok(acknowledged.Outcomes.Select(InstanceAckResponseDto.Create).ToList()),
                    // batch_too_large, per the design spec's error table: not retryable — the Bridge
                    // chunks against the counts published on GET /api/inventory/limits and resends.
                    AcknowledgeSpawnsResult.BatchTooLarge tooLarge => Results.Problem(
                        title: $"batch_too_large: {tooLarge.Requested} {tooLarge.Field} exceeds the maximum of {tooLarge.Max} per request",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: NotRetryableExtensions()),
                };
            })
            .RequireAuthorization(InventoryWritePolicy)
            .Produces<List<InstanceAckResponseDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("AcknowledgeInventorySpawns")
            .WithDescription(
                "Batched confirmation that one or more backend-granted instances were spawned, clearing "
                + "pendingSpawn on each. Adoption is mandatory: an id the backend never granted, or one "
                + "granted to a character not currently on the calling gameserver, is NotFound — never a "
                + "create. Idempotent: a replayed ack for an already-cleared instance returns "
                + "AlreadyCleared. Also mints any declared children (an engine-spawned magazine, SIM, "
                + "etc.), keyed by slot — a replay returns the same child ids rather than minting a "
                + "second set. A child naming an uncatalogued itemId is reported per-child, not a 500. "
                + "Bounded by count, not body size: more than maxAcksPerBatch entries, or more than "
                + "maxChildrenPerAck children on any one entry, is rejected whole as batch_too_large "
                + "(400, not retryable) — both caps are published on GET /api/inventory/limits.");

        // The Bridge write path (phase 2): validates a batch, then applies it under revision
        // last-write-wins in one transaction. See ApplySnapshotHandler's own doc comment for the full
        // validation order and the sole-minter rule.
        group.MapPost("snapshots", async (
                [FromBody] ApplySnapshotRequestDto request,
                IMediator mediator,
                ICurrentGameServer currentGameServer,
                CancellationToken cancellationToken) =>
            {
                var serverId = await currentGameServer.GetIdAsync(cancellationToken);

                if (!TryParseApplySnapshotCommand(serverId, request, out var command, out var parseProblem))
                {
                    return parseProblem!;
                }

                var result = await mediator.Send(command, cancellationToken);

                return ToProblemOrOk(result);
            })
            .RequireAuthorization(InventoryWritePolicy)
            .RequireRateLimiting(SnapshotRateLimitPolicy)
            .Produces<ApplySnapshotResponseDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("ApplyInventorySnapshot")
            .WithDescription(
                "The Bridge write path: the mod reports the current state of one or more item "
                + "instances, applied under revision last-write-wins in a single transaction. The "
                + "backend is the sole minter of an instanceId — an upsert naming an id it never "
                + "issued is rejected UnknownInstance and creates nothing, for every parent kind, with "
                + "no exception. A higher revision wins; a lower one is counted in `skippedNoOp`; an "
                + "equal revision carrying different content is an IdentityConflict rather than a "
                + "silent overwrite. Reporting a still-pending instance is treated as proof the mod "
                + "adopted it and clears pendingSpawn, which is how a lost ack recovers — but only from a "
                + "Character-scoped batch naming the character that instance is owed to, since an "
                + "undelivered grant has no delivery server of its own for the usual guard to check. "
                + "Moving a container rewrites the denormalised root fields of everything nested "
                + "inside it, and deleting one soft-deletes its contents with it — those cascaded rows are "
                + "reported in `cascadeDeleted` rather than added to `deleted`, which counts only the "
                + "deletes that were asked for, so both numbers stay meaningful. "
                + "Per-instance "
                + "problems (an uncatalogued itemId, an out-of-range revision/durability/ammo, an "
                + "instance on another gameserver, a container cycle or excessive nesting, an oversized "
                + "attribute bag, a staff-removed row, an unknown id) are reported in `rejected` and "
                + "never fail the batch. Only a duplicate instanceId within the batch, an over-sized "
                + "upserts/deletes array, an out-of-range `sequence`, or the batch's own scope not being "
                + "reachable from the calling gameserver fail the whole batch with `retryable: false` in "
                + "the problem detail. Replaying an already-applied `batchId` within "
                + "`batchIdRetentionSeconds` (see GET .../limits) returns the exact original response "
                + "with `replayOfPriorBatch: true` rather than re-applying anything — scoped to the "
                + "calling gameserver, so a batchId recorded under a different one is never returned "
                + "here. `mode: Full` additionally requires `sequence` (capped at a sanity ceiling well "
                + "above any realistic scheme) and is gated by a per-scope cursor, so an out-of-order "
                + "reconcile is rejected `stale_sequence` (409, not retryable); a `mode: Partial` batch "
                + "needs neither. `mode: Full` also MEANS 'this is everything in this scope': every "
                + "live row the scope holds that the payload does not mention is soft-deleted and "
                + "counted in `swept`, separately from `deleted` and `cascadeDeleted`. A `Full` must "
                + "enumerate the contents of any container it mentions; contents of a container it does "
                + "NOT mention are left alone, because silence about a container you never looked in is "
                + "not evidence of absence. Nor is anything swept that this same batch moved out of the "
                + "scope: report a backpack as dropped and its unreported contents go with it rather "
                + "than being deleted underneath it. Three further kinds of row are never swept — a "
                + "still-`pendingSpawn` instance (the game has not spawned it yet, so its absence from a "
                + "report of what the game can see means nothing), a staff-removed tombstone, and any "
                + "container a surviving row is still nested inside. "
                + "Its scope must be a single character or container: `mode: Full` with anything else "
                + "is `unsupported_full_scope` (400, not retryable), because a server-wide reconcile is "
                + "a separate staff operation with a dry run. Whichever kind `scope.kind` names, send "
                + "ONLY that kind's companion id — a scope carrying both `scope.characterId` and "
                + "`scope.containerInstanceId` names two anchors at once and is rejected 400 (not "
                + "retryable), for either mode; drop the one the declared kind does not use. And a "
                + "`Full` batch is refused when its scope HOLDS more than "
                + "`suspiciousReconcileScopeRowsThreshold` sweep-eligible rows (live, not pendingSpawn, "
                + "not staff-removed — this is a test of how much was at stake, NOT of how many rows the "
                + "batch would delete) AND it offers too little evidence for that: either fewer than "
                + "`suspiciousReconcileUpsertsThreshold` upserts, or a sweep covering at least "
                + "`suspiciousReconcileSweptPercentThreshold` percent of those same eligible rows (all "
                + "three on GET .../limits). Such a batch is refused "
                + "whole as `suspicious_reconcile` (422, not retryable), recorded for staff, and leaves "
                + "the scope's cursor unadvanced so a corrected reconcile is still accepted at the same "
                + "`sequence` — a server that booted with a failed mod load will happily report an "
                + "empty world, and soft delete is the only undo this design has. The one exception to "
                + "every rejection above being non-retryable: two "
                + "`Full` batches racing the same scope's cursor produce `concurrent_reconcile` (409, "
                + "`retryable: true`) for whichever commits second — an unmodified resend is correct. "
                + "Task 6 bounds the path: a token bucket partitioned on the caller's client_id, so "
                + "each gameserver gets its own burst allowance and its own sustained rate (both "
                + "published on GET /api/inventory/limits). Exceeding it is 429 with a Retry-After "
                + "header and `retryable: true` — the second retryable outcome this endpoint has, and "
                + "the only one that is about the caller's pace rather than its content.");

        group.MapPost("instances/{instanceId:guid}/spawn-failed", async (
                Guid instanceId,
                [FromBody] SpawnFailedRequestDto request,
                IMediator mediator,
                ICurrentGameServer currentGameServer,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseSpawnFailureReason(request.Reason, out var reason, out var problem))
                {
                    return problem;
                }

                var serverId = await currentGameServer.GetIdAsync(cancellationToken);
                var result = await mediator.Send(
                    new SpawnFailedCommand(serverId, new ItemInstanceId(instanceId), reason), cancellationToken);

                return result switch
                {
                    SpawnFailedResult.StillPending => Results.Ok(new SpawnFailedResponseDto { Outcome = "StillPending" }),
                    SpawnFailedResult.Undeliverable => Results.Ok(new SpawnFailedResponseDto { Outcome = "Undeliverable" }),
                    SpawnFailedResult.NotFound => Results.Problem(
                        title: "Item instance not found",
                        statusCode: StatusCodes.Status404NotFound,
                        extensions: NotRetryableExtensions()),
                    SpawnFailedResult.WrongServer => Results.Problem(
                        title: "Character is not on the calling gameserver",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                    SpawnFailedResult.RemovedByStaff => Results.Problem(
                        title: "Item instance was removed by staff",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                    SpawnFailedResult.NotPending => Results.Problem(
                        title: "Item instance is not pending delivery",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                };
            })
            .RequireAuthorization(InventoryWritePolicy)
            .Produces<SpawnFailedResponseDto>()
            // Whole-branch review: this endpoint has produced a 400 since phase 1 — an unrecognised
            // `reason` is rejected by TryParseSpawnFailureReason before the command is built — and had
            // never declared it, so a generated client had no model for the one rejection a mod is most
            // likely to hit while getting the enum spelling right.
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ReportInventorySpawnFailed")
            .WithDescription(
                "The negative ack: reports that a granted instance could not be spawned (inventory full, "
                + "missing prefab, missing container, or an entity type that can't adopt a backend id). "
                + "Ships in phase 1 because a portal purchase is delivered at join with no pre-flight "
                + "check possible. Never mutates pendingSpawn or deliveryAttempts — both are owned by the "
                + "pending-delivery read (GET .../pending) — this only reports which side of "
                + "maxDeliveryAttempts the instance already landed on.");

        group.MapGet("undeliverable", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var items = await mediator.Send(new UndeliverableInstancesQuery(), cancellationToken);
                return Results.Ok(items.Select(ItemInstanceDto.Create).ToList());
            })
            .RequireAuthorization(InventoryManagePolicy)
            .Produces<List<ItemInstanceDto>>()
            .WithName("GetUndeliverableInventoryInstances")
            .WithDescription(
                "The staff queue: instances that reached WorldSettings.MaxDeliveryAttempts without ever "
                + "being acked. Each row carries its origin/originRef so a human can redeliver or refund — "
                + "there is no automatic refund.");

        // Task 5: closes the feedback loop that makes "uncatalogued prefabs are not persisted"
        // survivable — see ItemNotInCatalogException and IUnknownPrefabSightingRepository's own doc
        // comment for the mechanism. No server guard (this reports a prefab class name, not anything
        // tied to a character or session) and no ItemInstance minting: this endpoint touches exactly one
        // hive-wide document family.
        //
        // Deliberately NOT rate-limited here — see this endpoint's own note below and the task 5
        // report: Task 6 owns attaching the policy that makes this safe against a mod bug flooding it.
        group.MapPost("unknown-prefabs", async (
                [FromBody] RecordUnknownPrefabSightingsRequestDto request,
                IMediator mediator,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseRecordUnknownPrefabSightingsCommand(request, timeProvider.GetUtcNow(), out var command, out var parseProblem))
                {
                    return parseProblem!;
                }

                var result = await mediator.Send(command, cancellationToken);
                return ToProblemOrAccepted(result);
            })
            .RequireAuthorization(InventoryWritePolicy)
            .RequireRateLimiting(UnknownPrefabRateLimitPolicy)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("RecordUnknownPrefabSightings")
            .WithDescription(
                "The mod's batched report of prefab class names it saw or picked up that have no "
                + "catalog entry — the only signal staff have that the catalog needs to grow. Upserts "
                + "one UnknownPrefabSighting per distinct prefabClassName, keyed on a deterministic id "
                + "derived from the name so the same prefab reported from any gameserver in the hive "
                + "accumulates onto one row: count is incremented by this report's count (never "
                + "replaced), lastSeenAt advances to the time this report was received, and "
                + "firstSeenAt/sampleContext are captured once, on the very first report, and never "
                + "overwritten afterward. prefabClassName and sampleContext are bounded strings and "
                + "count/firstSeenAt are bounded to a plausible range (see GET .../limits) — every "
                + "sighting in the batch is checked before any row is touched, and one out-of-bounds "
                + "entry fails the whole batch 400 (not retryable), same as an over-sized "
                + "sightings array. Task 6 attaches the aggressive rate-limiting policy task 5 asked "
                + "for: a token bucket partitioned on the caller's client_id, deliberately two orders "
                + "of magnitude tighter than the snapshot path's, because the thing it guards against "
                + "is a mod bug reporting the same missing prefab on every entity spawn — a loop that "
                + "produces well-formed, authorised, individually-cheap requests indefinitely, which "
                + "nothing else on this path would stop. Over it, 429 with Retry-After and "
                + "`retryable: true`; both the burst and the sustained rate are published on "
                + "GET /api/inventory/limits.");

        group.MapGet("unknown-prefabs", async (
                int? minCount,
                DateTimeOffset? since,
                int? offset,
                int? limit,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var sightings = await mediator.Send(new UnknownPrefabSightingsQuery(minCount, since, offset, limit), cancellationToken);
                return Results.Ok(sightings.Select(UnknownPrefabSightingDto.Create).ToList());
            })
            .RequireAuthorization(InventoryManagePolicy)
            .Produces<List<UnknownPrefabSightingDto>>()
            .WithName("GetUnknownPrefabSightings")
            .WithDescription(
                "The staff promotion queue: every reported prefab with no catalog entry, sorted by "
                + "count descending (ties broken by lastSeenAt descending, then id, for a stable total "
                + "order) so the ones worth cataloguing first sort to the top. Unlike phase 1's "
                + "GET .../undeliverable, this is genuinely paginated — limit is clamped to "
                + "[1, WorldSettings.MaxUnknownPrefabQueryPageSize] (published on GET .../limits), same "
                + "clamping shape as GET .../pending, and offset (default 0, floored at 0 and clamped "
                + "to WorldSettings.MaxUnknownPrefabQueryOffset — also on GET .../limits) moves through "
                + "the rest of the queue past the first page. minCount filters out noise below a chosen "
                + "threshold; since filters on lastSeenAt (not firstSeenAt), so a prefab last reported "
                + "long ago — its mod bug may since have been fixed — drops out even if it was first "
                + "seen recently.");

        // Task 7: gathering grants the item and its skill XP atomically in one commit — the two can
        // never diverge. A separate route group from api/inventory above: this isn't an inventory read
        // or a delivery acknowledgement, it's the other producer (alongside a shop purchase) of the
        // grants that flow through that same inventory surface.
        var gatheringGroup = app.MapGroup("api/gathering").WithTags("Gathering");

        gatheringGroup.MapPost("actions", async (
                [FromBody] GatherActionRequestDto request,
                IMediator mediator,
                ICurrentGameServer currentGameServer,
                CancellationToken cancellationToken) =>
            {
                var serverId = await currentGameServer.GetIdAsync(cancellationToken);
                var result = await mediator.Send(request.ToCommand(serverId), cancellationToken);

                return result switch
                {
                    GatherResult.Gathered gathered => Results.Ok(GatherActionResultDto.Create(gathered)),
                    GatherResult.CharacterNotFound => Results.Problem(
                        title: "Character not found",
                        statusCode: StatusCodes.Status404NotFound,
                        extensions: NotRetryableExtensions()),
                    GatherResult.WrongServer => Results.Problem(
                        title: "Character is not on the calling gameserver",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                    GatherResult.UnknownAction => Results.Problem(
                        title: "Unknown skill action",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: NotRetryableExtensions()),
                    GatherResult.InvalidQuantity invalidQuantity => Results.Problem(
                        title: $"quantity must be greater than zero, but was {invalidQuantity.Requested}",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: NotRetryableExtensions()),
                    GatherResult.GrantTooLarge grantTooLarge => Results.Problem(
                        title: $"Requested quantity {grantTooLarge.Requested} exceeds the maximum of {grantTooLarge.MaxInstancesPerGrant} instances per grant",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                    GatherResult.ItemNotInCatalog => Results.Problem(
                        title: "The gathered item no longer has a catalog entry to grant from",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: NotRetryableExtensions()),
                };
            })
            .RequireAuthorization(InventoryWritePolicy)
            .Produces<GatherActionResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("GatherItem")
            .WithDescription(
                "Records a gathering action for a character, granting the item and its skill XP "
                + "atomically in one commit so the two can never diverge. Returns the same "
                + "grantedInstances shape a shop purchase returns, so the mod's adopt-and-ack path is "
                + "written once and reused for both. Server-guarded like the ack path: a gather for a "
                + "character whose CurrentServerId is a different gameserver is 409, never a mint.");

        return app;
    }

    // No JsonStringEnumConverter is configured in this solution — enum-typed DTO properties would only
    // bind from their ordinal — so the spawn-failure reason crosses the wire as a string and is parsed
    // here. Same convention as ItemEndpoints' persistence parsing and ShopEndpoints' ownerType.
    private static bool TryParseSpawnFailureReason(string? raw, out SpawnFailureReason reason, out IResult? problem)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out reason) && Enum.IsDefined(reason))
        {
            problem = null;
            return true;
        }

        reason = default;
        problem = Results.Problem(
            title: $"reason must be one of: {string.Join(", ", Enum.GetNames<SpawnFailureReason>())}",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: NotRetryableExtensions());
        return false;
    }

    // Every enum on POST /api/inventory/snapshots' wire shape (scope.kind, mode, each upsert's
    // parent.kind, each delete's reason) is parsed here, same convention as TryParseSpawnFailureReason
    // above — no JsonStringEnumConverter is configured anywhere in this solution.
    //
    // Review round 1 (unknown-prefabs task): `required` on a DTO property does not stop System.Text.Json
    // deserializing an explicit JSON `null` into it, so `{ "scope": null }`/`{ "upserts": null }`/
    // `{ "upserts": [null] }` used to NRE straight into an unhandled-exception 500 instead of a 400 —
    // the same one-line defect shape TryParseRecordUnknownPrefabSightingsCommand had, closed there and
    // here together since this seam made both directly testable.
    internal static bool TryParseApplySnapshotCommand(
        GameServerId gameServerId,
        ApplySnapshotRequestDto request,
        out ApplySnapshotCommand command,
        out IResult? problem)
    {
        command = null!;

        if (request.Scope is null)
        {
            problem = BadRequest("scope is required");
            return false;
        }

        if (!Enum.TryParse<SnapshotScopeKind>(request.Scope.Kind, ignoreCase: true, out var scopeKind) || !Enum.IsDefined(scopeKind))
        {
            problem = BadRequest($"scope.kind must be one of: {string.Join(", ", Enum.GetNames<SnapshotScopeKind>())}");
            return false;
        }

        if (scopeKind == SnapshotScopeKind.Character && request.Scope.CharacterId is null)
        {
            problem = BadRequest("scope.characterId is required when scope.kind is Character");
            return false;
        }

        if (scopeKind == SnapshotScopeKind.Container && request.Scope.ContainerInstanceId is null)
        {
            problem = BadRequest("scope.containerInstanceId is required when scope.kind is Container");
            return false;
        }

        // ...and the other kind's companion id must be ABSENT, not merely unused (review round 2). A
        // scope naming two anchors is malformed on its face — it says "this batch is about a character"
        // and "this batch is about a container" at once — so it should never have parsed, and until now
        // it did: only the required id for the declared kind was checked, and both were copied into the
        // command regardless.
        //
        // That was not cosmetic. ApplySnapshotHandler's Full-mode sweep treats the scope container as a
        // container the payload has spoken about, which unlocks its contents for deletion; one stray
        // `scope.containerInstanceId` on a Character-scoped batch therefore deleted an unrelated crate's
        // entire contents, below the guard's thresholds and so without a staff record. The handler now
        // gates that on ScopeKind and is correct on its own, but a malformed scope has no business
        // reaching it in the first place — this is the outer of the two locks.
        if (scopeKind == SnapshotScopeKind.Character && request.Scope.ContainerInstanceId is not null)
        {
            problem = BadRequest("scope.containerInstanceId must not be set when scope.kind is Character");
            return false;
        }

        if (scopeKind == SnapshotScopeKind.Container && request.Scope.CharacterId is not null)
        {
            problem = BadRequest("scope.characterId must not be set when scope.kind is Container");
            return false;
        }

        if (!Enum.TryParse<SnapshotMode>(request.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            problem = BadRequest($"mode must be one of: {string.Join(", ", Enum.GetNames<SnapshotMode>())}");
            return false;
        }

        // Task 3: Full requires sequence — it is the ScopeCursor gate's own key input, and a null value
        // would have nothing to compare against. Partial needs none — see SnapshotMode.Full's own doc
        // comment on why a per-instance-revision-ordered Partial batch has no cursor to check.
        if (mode == SnapshotMode.Full && request.Sequence is null)
        {
            problem = BadRequest("sequence is required when mode is Full");
            return false;
        }

        if (request.Upserts is null)
        {
            problem = BadRequest("upserts must not be null");
            return false;
        }

        var upserts = new List<SnapshotUpsertRequest>(request.Upserts.Count);
        foreach (var upsert in request.Upserts)
        {
            if (upsert is null)
            {
                problem = BadRequest("upserts[] entries must not be null");
                return false;
            }

            if (upsert.Parent is null)
            {
                problem = BadRequest("upserts[].parent is required");
                return false;
            }

            // Review round 2 (unknown-prefabs task): Attributes carries a property *initializer*
            // (`= new Dictionary<string, string>()`), not `required` — but System.Text.Json only
            // consults an initializer for an *absent* key, exactly like `required` only guards presence.
            // An explicit `"attributes": null` still overwrites it, and this used to reach
            // ItemAttributes.Create(values) downstream with values == null, NREing on values.Count
            // instead of being rejected here.
            if (upsert.Attributes is null)
            {
                problem = BadRequest("upserts[].attributes must not be null");
                return false;
            }

            // Review round 3: the leaf one level deeper than the dictionary itself — a JSON object's
            // *keys* are always strings by grammar, but a *value* can be an explicit null
            // (`{"attributes":{"k":null}}`), which System.Text.Json happily deserializes into this
            // Dictionary<string, string> despite its declared value type having no nullable
            // annotation. This is the primary gate; ItemAttributes.Validate carries the same check as
            // a domain-level backstop in case a future leaf in this family is missed here again — see
            // that method's own comment.
            if (upsert.Attributes.Values.Any(value => value is null))
            {
                problem = BadRequest("upserts[].attributes values must not be null");
                return false;
            }

            if (!TryParseSnapshotParent(upsert.Parent, out var parentKind, out var parentCharacterId, out var slot, out var containerInstanceId, out var transform, out problem))
            {
                return false;
            }

            upserts.Add(new SnapshotUpsertRequest(
                new ItemInstanceId(upsert.InstanceId),
                upsert.Revision,
                new ItemId(upsert.ItemId),
                parentKind,
                parentCharacterId,
                slot,
                containerInstanceId,
                transform,
                upsert.Durability,
                upsert.Ammo,
                upsert.Attributes));
        }

        if (request.Deletes is null)
        {
            problem = BadRequest("deletes must not be null");
            return false;
        }

        var deletes = new List<SnapshotDeleteRequest>(request.Deletes.Count);
        foreach (var delete in request.Deletes)
        {
            if (delete is null)
            {
                problem = BadRequest("deletes[] entries must not be null");
                return false;
            }

            if (!Enum.TryParse<DeleteReason>(delete.Reason, ignoreCase: true, out var reason) || !Enum.IsDefined(reason))
            {
                problem = BadRequest($"deletes[].reason must be one of: {string.Join(", ", Enum.GetNames<DeleteReason>())}");
                return false;
            }

            deletes.Add(new SnapshotDeleteRequest(new ItemInstanceId(delete.InstanceId), delete.Revision, reason));
        }

        command = new ApplySnapshotCommand(
            gameServerId,
            request.BatchId,
            scopeKind,
            request.Scope.CharacterId is { } characterId ? new CharacterId(characterId) : null,
            request.Scope.ContainerInstanceId is { } scopeContainerId ? new ItemInstanceId(scopeContainerId) : null,
            request.Sequence,
            mode,
            upserts,
            deletes);
        problem = null;
        return true;
    }

    /// <summary>
    /// One upsert's <c>parent</c> variant, discriminated by <c>kind</c> — see
    /// <see cref="SnapshotParentRequestDto"/>'s own doc comment for which companion field each kind
    /// requires.
    /// </summary>
    private static bool TryParseSnapshotParent(
        SnapshotParentRequestDto parent,
        out ParentKind kind,
        out CharacterId? characterId,
        out string? slot,
        out ItemInstanceId? containerInstanceId,
        out WorldTransform? transform,
        out IResult? problem)
    {
        characterId = null;
        slot = parent.Slot;
        containerInstanceId = null;
        transform = null;

        if (!Enum.TryParse(parent.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind))
        {
            problem = BadRequest($"parent.kind must be one of: {string.Join(", ", Enum.GetNames<ParentKind>())}");
            return false;
        }

        switch (kind)
        {
            case ParentKind.Character:
                if (parent.CharacterId is not { } parentCharacterId)
                {
                    problem = BadRequest("parent.characterId is required when parent.kind is Character");
                    return false;
                }

                characterId = new CharacterId(parentCharacterId);
                break;

            case ParentKind.Container:
                if (parent.ContainerInstanceId is not { } parentContainerInstanceId)
                {
                    problem = BadRequest("parent.containerInstanceId is required when parent.kind is Container");
                    return false;
                }

                containerInstanceId = new ItemInstanceId(parentContainerInstanceId);
                break;

            case ParentKind.World:
                if (parent.Transform is null)
                {
                    problem = BadRequest("parent.transform is required when parent.kind is World");
                    return false;
                }

                // Review round 2 (unknown-prefabs task): Position/Rotation are `required` on
                // WorldTransformDto, which — same as everywhere else in this file — does not stop an
                // explicit JSON null from reaching here. `parent.Transform` being non-null was checked
                // above; that says nothing about its own two properties.
                if (parent.Transform.Position is null)
                {
                    problem = BadRequest("parent.transform.position is required when parent.kind is World");
                    return false;
                }

                if (parent.Transform.Rotation is null)
                {
                    problem = BadRequest("parent.transform.rotation is required when parent.kind is World");
                    return false;
                }

                transform = new WorldTransform(
                    new WorldVector3(parent.Transform.Position.X, parent.Transform.Position.Y, parent.Transform.Position.Z),
                    new WorldVector3(parent.Transform.Rotation.X, parent.Transform.Rotation.Y, parent.Transform.Rotation.Z));
                break;
        }

        problem = null;
        return true;
    }

    private static IResult BadRequest(string title) => Results.Problem(
        title: title,
        statusCode: StatusCodes.Status400BadRequest,
        extensions: NotRetryableExtensions());

    /// <summary>
    /// Maps one <see cref="ApplySnapshotResult"/> onto the HTTP response the design spec's error table
    /// calls for. Lifted out of the endpoint lambda (review round 3) purely so it can be tested: it is a
    /// pure function of a union value with no HTTP, DI or database dependence, and the only thing that
    /// had ever stopped it being asserted on directly was that it lived inside a closure.
    ///
    /// That matters more than it sounds. Three separate findings across this phase — task 1's
    /// <c>retryable</c> flag, task 3's result-to-<see cref="IResult"/> mapping, and task 4's
    /// malformed-scope rejection — were all of exactly this shape: pure DTO-or-union functions sitting
    /// untested behind <c>private static</c>. See <c>World.Api</c>'s <c>AssemblyInfo.cs</c> for the seam
    /// and why it is preferred to a host harness.
    ///
    /// Deliberately exhaustive with no default arm: the compiler reports <c>CS8509</c> naming any union
    /// case a future task forgets, which is what stops a new outcome silently falling through to a 500.
    /// </summary>
    internal static IResult ToProblemOrOk(ApplySnapshotResult result)
        => result switch
        {
            ApplySnapshotResult.Applied applied => Results.Ok(ApplySnapshotResponseDto.Create(applied)),
            // duplicate_instance_id, per the design spec's error table: not retryable — the
            // same id twice in one batch is a client-side bug (likely entity cloning), and
            // retrying the identical payload reproduces the exact same rejection.
            ApplySnapshotResult.DuplicateInstanceId duplicate => Results.Problem(
                title: $"duplicate_instance_id: instance '{duplicate.InstanceId}' appears more than once in this batch",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: NotRetryableExtensions()),
            // batch_too_large, per the design spec's error table: not retryable — the Bridge
            // chunks against the counts published on GET /api/inventory/limits and resends.
            ApplySnapshotResult.BatchTooLarge tooLarge => Results.Problem(
                title: $"batch_too_large: {tooLarge.Requested} {tooLarge.Field} exceeds the maximum of {tooLarge.Max} per request",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: NotRetryableExtensions()),
            // wrong_server, per the design spec's error table: not retryable. Deliberately
            // carries no actualServerId — see ApplySnapshotResult.WrongServer's own doc
            // comment: naming the other gameserver would leak where a character went.
            ApplySnapshotResult.WrongServer => Results.Problem(
                title: "wrong_server: the batch's scope is not reachable from the calling gameserver",
                statusCode: StatusCodes.Status409Conflict,
                extensions: NotRetryableExtensions()),
            // stale_sequence, per the design spec's error table: not retryable — a Full batch
            // whose sequence has already been superseded for this scope. Carries
            // lastAppliedSequence so the Bridge can tell how far behind it fell.
            ApplySnapshotResult.StaleSequence stale => Results.Problem(
                title: $"stale_sequence: sequence must be greater than the last applied sequence {stale.LastAppliedSequence} for this scope",
                statusCode: StatusCodes.Status409Conflict,
                extensions: NotRetryableExtensions(stale.LastAppliedSequence)),
            // sequence_out_of_range, fix round 1 item 1 (ceiling) + fix round 2 item 4 (the
            // symmetric lower bound): not retryable — a Full batch naming a sequence outside
            // 0..ScopeCursor.MaxSequence is a client-side bug (or a poisoned counter), and
            // retrying the identical value reproduces the exact same rejection.
            ApplySnapshotResult.SequenceOutOfRange outOfRange => Results.Problem(
                title: $"sequence_out_of_range: {outOfRange.Requested} must be within 0..{outOfRange.Max}",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: NotRetryableExtensions()),
            // concurrent_reconcile, fix round 1 item 7: the one RETRYABLE outcome on this
            // endpoint — two Full batches raced this scope's cursor and this one lost, through
            // no fault of its own. A plain, unmodified resend is the correct Bridge response, so
            // this is deliberately the one Results.Problem on this endpoint that does NOT come
            // from NotRetryableExtensions() — see that helper's own doc comment.
            ApplySnapshotResult.ConcurrentReconcile => Results.Problem(
                title: "concurrent_reconcile: another Full reconcile committed first for this scope — retry unmodified",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["retryable"] = true }),
            // unsupported_full_scope, task 4: not retryable — a Full reconcile has to name one
            // bounded Character or Container, because Full now DELETES what its payload omits
            // and an unbounded Full is a deployment-wide wipe. A server-wide reconcile is a
            // separate, explicitly-authorised staff operation with a dry run, not a field a
            // gameserver can widen on an ordinary batch.
            ApplySnapshotResult.UnsupportedFullScope unsupported => Results.Problem(
                title: $"unsupported_full_scope: mode Full requires a single Character or Container scope, not '{unsupported.ScopeKind}'",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: NotRetryableExtensions()),
            // suspicious_reconcile, task 4: the data-loss guard. 422 rather than 400 or 409 —
            // nothing is malformed and nothing raced; this is a valid batch whose claim the
            // backend declines to act on. Not retryable, because resending it reproduces the
            // identical refusal: the guard is a pure function of the batch and the scope's
            // contents, and the contents are unchanged precisely because this was refused. A
            // SuspiciousReconcile record is waiting for staff, and the scope's cursor was NOT
            // advanced, so the corrected reconcile is still accepted at this same sequence.
            // The noun in this sentence has to match the number, and getting that wrong misleads
            // precisely the person least able to check it. These counts are over SWEEP-ELIGIBLE rows —
            // live, not pendingSpawn, not staff-removed — so for a character holding 30 undelivered
            // grants and 2 carried items, "the 2 rows in its scope" would be read by a staff member
            // looking at 32 as either a bug or a lie. A knob's name can lean on its doc comment; a
            // sentence a human reads while working out what happened cannot. (Review round 4.)
            ApplySnapshotResult.SuspiciousReconcile suspicious => Results.Problem(
                title: $"suspicious_reconcile: this Full batch would have deleted {suspicious.WouldHaveSwept} "
                    + $"of the {suspicious.ScopeRowCount} sweep-eligible rows in its scope (excludes undelivered "
                    + $"grants and staff-removed rows; guarded above {suspicious.ScopeRowsThreshold} such rows) "
                    + $"while offering too little evidence for it — only {suspicious.Upserts} upserts (threshold "
                    + $"{suspicious.UpsertsThreshold}), or a sweep covering at least "
                    + $"{suspicious.SweptPercentThreshold}% of those eligible rows — refused and "
                    + "recorded for staff review",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: NotRetryableExtensions()),
        };


    /// <summary>
    /// Task 5's shape validation for <c>POST /api/inventory/unknown-prefabs</c> — every bound named in
    /// the task brief (<c>count</c>, <c>firstSeenAt</c>, <c>prefabClassName</c>, <c>sampleContext</c>)
    /// checked here, before a single row is touched, same discipline as
    /// <see cref="TryParseApplySnapshotCommand"/>. A pure function of the DTO and the caller's own
    /// clock (<paramref name="now"/> is passed in rather than read from <see cref="TimeProvider"/>
    /// directly, precisely so this stays testable without one) — no HTTP, DI, or database dependence.
    ///
    /// The one check this endpoint does <b>not</b> make here is the batch-size cap
    /// (<c>WorldSettings.MaxUnknownPrefabSightingsPerBatch</c>): that depends on a runtime-configurable
    /// setting this pure function has no access to, so it is <see cref="RecordUnknownPrefabSightingsHandler"/>'s
    /// job instead — same split <see cref="AcknowledgeSpawnsHandler"/> uses for its own batch cap.
    ///
    /// Review round 1: <c>required</c> on a DTO property does not stop System.Text.Json from
    /// deserializing an explicit JSON <c>null</c> into it — it only enforces the key being present, not
    /// the value being non-null. A <c>{ "sightings": null }</c> or <c>{ "sightings": [null] }</c> body
    /// used to reach <c>request.Sightings.Count</c>/<c>request.Sightings[i]</c> and NRE into an
    /// unhandled-exception 500 instead of a 400 — closed here, and the same-shaped defect in
    /// <see cref="TryParseApplySnapshotCommand"/> was closed alongside it while this seam was open.
    /// </summary>
    internal static bool TryParseRecordUnknownPrefabSightingsCommand(
        RecordUnknownPrefabSightingsRequestDto request,
        DateTimeOffset now,
        out RecordUnknownPrefabSightingsCommand command,
        out IResult? problem)
    {
        command = null!;

        if (request.Sightings is null)
        {
            problem = BadRequest("sightings must not be null");
            return false;
        }

        var parsed = new List<UnknownPrefabSightingRequest>(request.Sightings.Count);

        for (var i = 0; i < request.Sightings.Count; i++)
        {
            var sighting = request.Sightings[i];
            if (sighting is null)
            {
                problem = BadRequest($"sightings[{i}] must not be null");
                return false;
            }

            var prefabClassName = sighting.PrefabClassName?.Trim();

            if (string.IsNullOrEmpty(prefabClassName))
            {
                problem = BadRequest($"sightings[{i}].prefabClassName must not be empty");
                return false;
            }

            if (prefabClassName.Length > UnknownPrefabSighting.MaxPrefabClassNameLength)
            {
                problem = BadRequest(
                    $"sightings[{i}].prefabClassName must be at most {UnknownPrefabSighting.MaxPrefabClassNameLength} characters, but was {prefabClassName.Length}");
                return false;
            }

            if (sighting.Count is < 1 or > UnknownPrefabSighting.MaxCountPerSighting)
            {
                problem = BadRequest(
                    $"sightings[{i}].count must be within 1..{UnknownPrefabSighting.MaxCountPerSighting}, but was {sighting.Count}");
                return false;
            }

            // A small allowance ahead of "now" absorbs ordinary clock skew between a gameserver and
            // this API without opening the door to an arbitrary future value; the 30-day floor rejects
            // garbage (a zeroed/epoch DateTimeOffset, a corrupted clock) while still tolerating a mod's
            // local buffer genuinely holding a sighting for a while before it flushes.
            if (sighting.FirstSeenAt > now + MaxFirstSeenAtClockSkewAhead)
            {
                problem = BadRequest($"sightings[{i}].firstSeenAt must not be in the future");
                return false;
            }

            if (sighting.FirstSeenAt < now - MaxFirstSeenAtAge)
            {
                problem = BadRequest($"sightings[{i}].firstSeenAt must be within the last {MaxFirstSeenAtAge.TotalDays:0} days");
                return false;
            }

            // Trimmed, and an empty result normalized to null (review round 1) — "" and null both mean
            // "the mod had nothing to say," and storing "" as a distinct value would just be a second
            // spelling of the same absence for every reader of GET .../unknown-prefabs to handle.
            // Normalizing first also makes the length guard below unconditional: after this, sampleContext
            // is either null or a non-empty string, so there is nothing left for a redundant
            // "is it non-empty" check to guard against.
            var sampleContext = sighting.SampleContext?.Trim();
            if (string.IsNullOrEmpty(sampleContext))
            {
                sampleContext = null;
            }
            else if (sampleContext.Length > UnknownPrefabSighting.MaxSampleContextLength)
            {
                problem = BadRequest(
                    $"sightings[{i}].sampleContext must be at most {UnknownPrefabSighting.MaxSampleContextLength} characters, but was {sampleContext.Length}");
                return false;
            }

            parsed.Add(new UnknownPrefabSightingRequest(prefabClassName, sighting.Count, sighting.FirstSeenAt, sampleContext));
        }

        command = new RecordUnknownPrefabSightingsCommand(parsed);
        problem = null;
        return true;
    }

    private static readonly TimeSpan MaxFirstSeenAtClockSkewAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxFirstSeenAtAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Maps <see cref="RecordUnknownPrefabSightingsResult"/> onto the endpoint's HTTP response —
    /// extracted for the same reason <see cref="ToProblemOrOk"/> is (see that method's own doc
    /// comment): a pure function of a union value, directly testable without a host.
    /// </summary>
    internal static IResult ToProblemOrAccepted(RecordUnknownPrefabSightingsResult result)
        => result switch
        {
            RecordUnknownPrefabSightingsResult.Recorded => Results.Accepted(),
            // batch_too_large: not retryable — the Bridge chunks against
            // WorldSettings.MaxUnknownPrefabSightingsPerBatch (GET .../limits) and resends.
            RecordUnknownPrefabSightingsResult.BatchTooLarge tooLarge => Results.Problem(
                title: $"batch_too_large: {tooLarge.Requested} sightings exceeds the maximum of {tooLarge.Max} per request",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: NotRetryableExtensions()),
        };

    // A fresh dictionary per call — every error this MODULE returns THROUGH THIS HELPER carries
    // `retryable: false`, per the design spec's error table: a store-and-forward Bridge needs this to
    // tell "already done, drop it" / "you're behind, drop it" from "retry later", and none of these are
    // the latter. `lastAppliedSequence` (task 3's `stale_sequence`) is the one other value this module
    // ever needs to carry alongside `retryable: false`, so it is an optional addition to the same
    // dictionary rather than a second, separately-constructed one.
    //
    // Task 6 widened this from "every problem the snapshot and unknown-prefab endpoints return" to
    // "every problem this module returns" — the phase 1 endpoints (acks, spawn-failed, gathering) were
    // written before the flag existed and returned bare problems, so a client reading the flag saw it
    // on some of this module's rejections and not others. A partial signal is worse than none: an
    // implementer either has to keep a list of which endpoints carry it, or has to treat "absent" as
    // ambiguous. Every one of those cases was already non-retryable on its own merits (an unknown
    // instance, a character on another server, a quantity out of range, an uncatalogued item — each
    // reproduces exactly on resend), so this is a documentation fix expressed in code rather than a
    // behaviour change, and it is what lets docs/bridge.md state the rule without an exception list.
    //
    // The helper stays deliberately incapable of emitting `retryable: true` — see the note below.
    //
    // Fix round 1, item 7 breaks the "only place extensions is constructed" claim this comment used to
    // make: `concurrent_reconcile` is genuinely retryable (`{"retryable": true}`), the one case on this
    // endpoint where reusing this non-retryable-only helper would be actively wrong, so it builds its
    // own tiny dictionary inline at its one call site instead — deliberately never funnelled through
    // here, so it can never be mistaken for one of the non-retryable cases this helper exists for.
    private static Dictionary<string, object?> NotRetryableExtensions(long? lastAppliedSequence = null)
    {
        var extensions = new Dictionary<string, object?> { ["retryable"] = false };
        if (lastAppliedSequence is { } sequence)
        {
            extensions["lastAppliedSequence"] = sequence;
        }

        return extensions;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
