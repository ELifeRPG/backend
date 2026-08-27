using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// The negative-ack escape hatch. Never mutates <see cref="ItemInstance.PendingSpawn"/> or
/// <see cref="ItemInstance.DeliveryAttempts"/> — both are owned entirely by the read side
/// (<c>PendingDeliveriesHandler</c> increments the latter the moment a row is served; task 4).
/// <see cref="StillPending"/> vs <see cref="Undeliverable"/> is purely informational: it tells the
/// caller which side of <c>WorldSettings.MaxDeliveryAttempts</c> the row already landed on, derived
/// fresh on every call rather than stored — see <c>IItemInstanceRepository.FindUndeliverableAsync</c>'s
/// doc comment. What *is* persisted (review round 1, B-3) is <see cref="ItemInstance.LastSpawnFailureReason"/>,
/// <see cref="ItemInstance.LastSpawnFailureAt"/> and <see cref="ItemInstance.SpawnFailureCount"/> — see
/// <see cref="ItemInstance.RecordSpawnFailure"/>: without them, a mod reporting <c>InventoryFull</c> and
/// a mod silently dropping the item left the backend in byte-identical states, and the reason is
/// exactly what tells staff whether a row on the undeliverable queue is worth redelivering or needs the
/// mod fixed first and the purchase refunded instead.
/// </summary>
public union SpawnFailedResult(SpawnFailedResult.StillPending, SpawnFailedResult.Undeliverable, SpawnFailedResult.NotFound, SpawnFailedResult.WrongServer, SpawnFailedResult.RemovedByStaff, SpawnFailedResult.NotPending)
{
    /// <summary>Below the delivery cap — the mod (or a future login) will be offered this instance again.</summary>
    public record StillPending;

    /// <summary>At or beyond the delivery cap — the instance now surfaces on <c>GET /api/inventory/undeliverable</c> for staff to redeliver or refund. Never an automatic refund.</summary>
    public record Undeliverable;

    /// <summary>The id was never granted by the backend. See <see cref="AckOutcome.NotFound"/>'s doc comment — same reasoning.</summary>
    public record NotFound;

    /// <summary>The id was granted, but to a character not currently on the calling gameserver. See <see cref="AckOutcome.WrongServer"/>'s doc comment — same split, added in review round 1 (B-4) for the same reason.</summary>
    public record WrongServer;

    public record RemovedByStaff;

    /// <summary>
    /// The instance is not (or no longer) <see cref="ItemInstance.PendingSpawn"/> — already spawned and
    /// acked, so there is nothing to report a spawn failure against. Added in review round 2, item (i):
    /// once B-3 made this handler persist a reason/timestamp/count, an unguarded call let an
    /// authenticated gameserver drive those counters on *any* known instance id, including an
    /// already-delivered one, and <see cref="StillPending"/> would have been a lie for it. Nothing is
    /// recorded when this is returned.
    /// </summary>
    public record NotPending;
}

/// <summary>Backs <c>POST /api/inventory/instances/{instanceId}/spawn-failed</c>.</summary>
public sealed record SpawnFailedCommand(GameServerId GameServerId, ItemInstanceId InstanceId, SpawnFailureReason Reason)
    : IRequest<SpawnFailedResult>;

/// <summary>
/// Server-guarded the same way <see cref="AcknowledgeSpawnsHandler"/> is, on a single instance rather
/// than a batch. This ships in phase 1, not later: a portal purchase is delivered at join with no
/// pre-flight check possible, so the negative ack is the only way "it didn't fit" becomes a retry
/// instead of leaving the row pending forever with no visibility into why.
/// </summary>
public sealed class SpawnFailedHandler(
    IItemInstanceRepository repository,
    IWorldSettingsRepository settingsRepository,
    IMediator mediator,
    TimeProvider timeProvider)
    : IRequestHandler<SpawnFailedCommand, SpawnFailedResult>
{
    public async ValueTask<SpawnFailedResult> Handle(SpawnFailedCommand request, CancellationToken cancellationToken)
    {
        var instance = await repository.FindByIdAsync(request.InstanceId, cancellationToken);
        if (instance is null)
        {
            return new SpawnFailedResult.NotFound();
        }

        if (instance.RemovedByStaff)
        {
            return new SpawnFailedResult.RemovedByStaff();
        }

        if (instance.RootCharacterId is not { } rootCharacterId)
        {
            return new SpawnFailedResult.WrongServer();
        }

        var onThisServer = await mediator.Send(
            new CharactersOnServerQuery(request.GameServerId, [rootCharacterId]), cancellationToken);
        if (!onThisServer.Contains(rootCharacterId))
        {
            return new SpawnFailedResult.WrongServer();
        }

        // Review round 2, item (i): a spawn-failed report only makes sense against a row still awaiting
        // delivery. Without this, an already-acked instance's SpawnFailureCount/UpdatedAt could be
        // driven without bound by any caller who still knows its id, and StillPending/Undeliverable
        // would be reporting on a counter (DeliveryAttempts) that no longer describes this row's
        // situation at all.
        if (!instance.PendingSpawn)
        {
            return new SpawnFailedResult.NotPending();
        }

        var settings = await settingsRepository.GetAsync(cancellationToken);

        // Persisted regardless of which side of the cap this lands on — a row that's about to become
        // undeliverable still deserves its final failure reason recorded, and a row that's still
        // pending benefits just as much from staff being able to see prior failed attempts if it later
        // does hit the cap.
        //
        // An atomic patch of just those three fields (plus UpdatedAt), never repository.Store: this
        // handler read the row a moment ago and a concurrent ack may have cleared PendingSpawn since,
        // in which case a whole-document write would resurrect it. See
        // IItemInstanceRepository.RecordSpawnFailure.
        repository.RecordSpawnFailure(instance, request.Reason, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        return instance.DeliveryAttempts >= settings.MaxDeliveryAttempts
            ? new SpawnFailedResult.Undeliverable()
            : new SpawnFailedResult.StillPending();
    }
}
