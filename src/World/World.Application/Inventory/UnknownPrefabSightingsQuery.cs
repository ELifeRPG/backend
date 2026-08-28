using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Inventory;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// Backs <c>GET /api/inventory/unknown-prefabs?minCount=&amp;since=&amp;offset=&amp;limit=</c> — the
/// staff promotion queue, sorted by <see cref="UnknownPrefabSighting.Count"/> descending so the prefabs
/// worth cataloguing first sort to the top (ties broken by <c>LastSeenAt</c> descending, then by
/// <c>Id</c> for a total order — see <c>MartenUnknownPrefabSightingRepository.FindForStaffAsync</c>).
/// Unlike phase 1's <c>GET /api/inventory/undeliverable</c> (flat, unpaginated), this is genuinely
/// paged: <paramref name="Limit"/> is clamped to <c>[1, WorldSettings.MaxUnknownPrefabQueryPageSize]</c>
/// by the handler, same clamping shape as <see cref="PendingDeliveriesQuery"/>, and
/// <paramref name="Offset"/> (review round 1 — the first cut took only <c>limit</c>, which left staff no
/// way to see anything past the first page) lets a caller move through the rest, floored at <c>0</c> and
/// clamped above to <see cref="WorldSettings.MaxUnknownPrefabQueryOffset"/> (review round 2: floored but
/// previously unbounded above, which let a single request force an arbitrarily large Postgres index
/// scan). A stable total order is what makes that offset mean the same row set across two calls with the
/// same filters — without the <c>Id</c> tiebreaker, ties on <c>Count</c>/<c>LastSeenAt</c> have no
/// defined order across calls, and a hard <c>Take</c> could silently drop or duplicate rows between
/// pages.
///
/// <paramref name="Since"/> filters on <see cref="UnknownPrefabSighting.LastSeenAt"/>, not
/// <see cref="UnknownPrefabSighting.FirstSeenAt"/> — a deliberate choice: the queue's purpose is
/// deciding what to catalogue *now*, and a prefab last reported months ago (the mod bug that produced
/// it may since have been fixed) is a worse use of staff attention than one still actively being
/// reported, even if both were first seen on the same day.
/// </summary>
public sealed record UnknownPrefabSightingsQuery(int? MinCount, DateTimeOffset? Since, int? Offset, int? Limit)
    : IRequest<IReadOnlyList<UnknownPrefabSighting>>;

public sealed class UnknownPrefabSightingsHandler(IUnknownPrefabSightingRepository repository, IWorldSettingsRepository settingsRepository)
    : IRequestHandler<UnknownPrefabSightingsQuery, IReadOnlyList<UnknownPrefabSighting>>
{
    public async ValueTask<IReadOnlyList<UnknownPrefabSighting>> Handle(UnknownPrefabSightingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken);
        var limit = request.Limit is > 0
            ? Math.Min(request.Limit.Value, settings.MaxUnknownPrefabQueryPageSize)
            : settings.MaxUnknownPrefabQueryPageSize;
        var offset = request.Offset is > 0
            ? Math.Min(request.Offset.Value, settings.MaxUnknownPrefabQueryOffset)
            : 0;

        return await repository.FindForStaffAsync(request.MinCount, request.Since, offset, limit, cancellationToken);
    }
}
