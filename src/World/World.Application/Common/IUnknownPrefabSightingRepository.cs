using ELifeRPG.World.Domain.Inventory;

namespace ELifeRPG.World.Application.Common;

public interface IUnknownPrefabSightingRepository
{
    /// <summary>
    /// Queues the upsert for one reported sighting onto the repository's shared session — see
    /// <c>MartenUnknownPrefabSightingRepository</c>'s implementation for the mechanism. Nothing is sent
    /// to Postgres until <see cref="SaveChangesAsync"/> runs, so a whole batch of sightings can be
    /// queued here and flushed together in one round trip.
    ///
    /// <paramref name="prefabClassName"/> is trimmed internally before it is hashed into an id or
    /// stored, so a caller does not need to pre-trim — review round 1: the id and the stored value must
    /// agree on the same trimmed name, or a row's <c>PrefabClassName</c> could carry whitespace its own
    /// id was derived without.
    /// </summary>
    void RecordSighting(string prefabClassName, int count, DateTimeOffset firstSeenAt, string? sampleContext, DateTimeOffset now);

    /// <summary>
    /// Backs the staff promotion queue — sorted by <see cref="UnknownPrefabSighting.Count"/> descending
    /// (ties broken by <c>LastSeenAt</c> descending, then by <c>Id</c> for a total order — see the
    /// implementation), filtered by <paramref name="minCount"/>/<paramref name="since"/> and paged by
    /// <paramref name="offset"/>/<paramref name="limit"/>, both already validated/clamped by the handler.
    /// </summary>
    ValueTask<IReadOnlyList<UnknownPrefabSighting>> FindForStaffAsync(
        int? minCount, DateTimeOffset? since, int offset, int limit, CancellationToken cancellationToken);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
