using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>One reported sighting — see <see cref="RecordUnknownPrefabSightingsCommand"/>. Every field's bound is enforced by <c>WorldModule.TryParseRecordUnknownPrefabSightingsCommand</c> before this record is ever constructed.</summary>
public sealed record UnknownPrefabSightingRequest(string PrefabClassName, int Count, DateTimeOffset FirstSeenAt, string? SampleContext);

/// <summary>
/// <see cref="BatchTooLarge"/> is the only rejection this command can produce — every per-field bound
/// (string lengths, the count ceiling, timestamp plausibility) is a pure shape check the endpoint's
/// <c>TryParseRecordUnknownPrefabSightingsCommand</c> already enforced before dispatch, so nothing
/// reaching this handler can fail for a per-sighting reason. The batch-size cap is the one check that
/// belongs here instead: it depends on <c>WorldSettings</c>, a runtime-configurable value the endpoint's
/// pure parse function has no access to — same split as <c>AcknowledgeSpawnsHandler</c>'s
/// <c>MaxAcksPerBatch</c> check.
/// </summary>
public union RecordUnknownPrefabSightingsResult(RecordUnknownPrefabSightingsResult.Recorded, RecordUnknownPrefabSightingsResult.BatchTooLarge)
{
    public record Recorded(int SightingsRecorded);

    /// <summary><c>batch_too_large</c>, per the same convention as every other batched write in this module: not retryable — the Bridge chunks against <c>WorldSettings.MaxUnknownPrefabSightingsPerBatch</c> (published on <c>GET /api/inventory/limits</c>) and resends.</summary>
    public record BatchTooLarge(int Requested, int Max);
}

/// <summary>
/// Backs <c>POST /api/inventory/unknown-prefabs</c> — the feedback loop that makes "uncatalogued
/// prefabs are not persisted" survivable (task 5). No server guard and no <c>ItemInstance</c> minting:
/// this command touches exactly one document family
/// (<see cref="ELifeRPG.World.Domain.Inventory.UnknownPrefabSighting"/>), a hive-wide running tally with
/// no character, session, or gameserver ownership of its own — see that type's own doc comment.
/// </summary>
public sealed record RecordUnknownPrefabSightingsCommand(IReadOnlyList<UnknownPrefabSightingRequest> Sightings)
    : IRequest<RecordUnknownPrefabSightingsResult>;

public sealed class RecordUnknownPrefabSightingsHandler(
    IUnknownPrefabSightingRepository repository,
    IWorldSettingsRepository settingsRepository,
    TimeProvider timeProvider)
    : IRequestHandler<RecordUnknownPrefabSightingsCommand, RecordUnknownPrefabSightingsResult>
{
    public async ValueTask<RecordUnknownPrefabSightingsResult> Handle(RecordUnknownPrefabSightingsCommand request, CancellationToken cancellationToken)
    {
        if (request.Sightings.Count == 0)
        {
            return new RecordUnknownPrefabSightingsResult.Recorded(0);
        }

        // Count cap first, before a single row is touched — same discipline as every other batched
        // write in this module (AcknowledgeSpawnsHandler, ApplySnapshotHandler): the cap doubles as the
        // lock-duration bound on the transaction this handler is about to open.
        var settings = await settingsRepository.GetAsync(cancellationToken);
        if (request.Sightings.Count > settings.MaxUnknownPrefabSightingsPerBatch)
        {
            return new RecordUnknownPrefabSightingsResult.BatchTooLarge(request.Sightings.Count, settings.MaxUnknownPrefabSightingsPerBatch);
        }

        // Every sighting is queued onto the same shared session and flushed in the one SaveChangesAsync
        // below — one round trip for the whole batch, not one per sighting. See
        // IUnknownPrefabSightingRepository.RecordSighting's own doc comment for why a single sighting's
        // upsert itself is also one round trip.
        var now = timeProvider.GetUtcNow();
        foreach (var sighting in request.Sightings)
        {
            repository.RecordSighting(sighting.PrefabClassName, sighting.Count, sighting.FirstSeenAt, sighting.SampleContext, now);
        }

        await repository.SaveChangesAsync(cancellationToken);

        return new RecordUnknownPrefabSightingsResult.Recorded(request.Sightings.Count);
    }
}
