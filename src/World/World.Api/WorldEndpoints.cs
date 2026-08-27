using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Api.Common;
using ELifeRPG.World.Api.Gathering;
using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Gathering;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
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
            .AddPolicy(InventoryManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, InventoryManageScope)));

        return services;
    }

    public static WebApplication MapWorldModule(this WebApplication app)
    {
        var group = app.MapGroup("api/inventory").WithTags("Inventory");

        group.MapGet("limits", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var settings = await mediator.Send(new WorldSettingsQuery(), cancellationToken);
                return Results.Ok(WorldLimitsDto.Create(settings));
            })
            .RequireAuthorization(InventoryReadPolicy)
            .Produces<WorldLimitsDto>()
            .WithName("GetInventoryLimits")
            .WithDescription(
                "Reports the operationally tunable grant/delivery settings alongside the structural "
                + "domain constants (container depth, attribute limits), so the Bridge hardcodes nothing.");

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
                        statusCode: StatusCodes.Status400BadRequest),
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
                    SpawnFailedResult.NotFound => Results.Problem(title: "Item instance not found", statusCode: StatusCodes.Status404NotFound),
                    SpawnFailedResult.WrongServer => Results.Problem(
                        title: "Character is not on the calling gameserver", statusCode: StatusCodes.Status409Conflict),
                    SpawnFailedResult.RemovedByStaff => Results.Problem(
                        title: "Item instance was removed by staff", statusCode: StatusCodes.Status409Conflict),
                    SpawnFailedResult.NotPending => Results.Problem(
                        title: "Item instance is not pending delivery", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(InventoryWritePolicy)
            .Produces<SpawnFailedResponseDto>()
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
                    GatherResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    GatherResult.WrongServer => Results.Problem(
                        title: "Character is not on the calling gameserver", statusCode: StatusCodes.Status409Conflict),
                    GatherResult.UnknownAction => Results.Problem(title: "Unknown skill action", statusCode: StatusCodes.Status400BadRequest),
                    GatherResult.InvalidQuantity invalidQuantity => Results.Problem(
                        title: $"quantity must be greater than zero, but was {invalidQuantity.Requested}",
                        statusCode: StatusCodes.Status400BadRequest),
                    GatherResult.GrantTooLarge grantTooLarge => Results.Problem(
                        title: $"Requested quantity {grantTooLarge.Requested} exceeds the maximum of {grantTooLarge.MaxInstancesPerGrant} instances per grant",
                        statusCode: StatusCodes.Status409Conflict),
                    GatherResult.ItemNotInCatalog => Results.Problem(
                        title: "The gathered item no longer has a catalog entry to grant from", statusCode: StatusCodes.Status409Conflict),
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
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
