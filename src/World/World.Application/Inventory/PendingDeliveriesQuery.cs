using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// Backs <c>GET /api/inventory/characters/{characterId}/pending?limit=</c> — the bounded,
/// oldest-first queue of instances owed but not yet spawned. <paramref name="Limit"/> is clamped to
/// <c>[1, WorldSettings.MaxPendingPageSize]</c> by the handler; a null or out-of-range value falls
/// back to the page size itself, which doubles as the default per <c>WorldSettings.MaxPendingPageSize</c>'s
/// doc comment.
///
/// Dispatching this query is not a side-effect-free peek: every row it returns has just had its
/// <see cref="ItemInstance.DeliveryAttempts"/> incremented — see <see cref="PendingDeliveriesHandler"/>.
/// That is deliberate (a row is "served" the moment it is handed to the mod), and is exactly why this
/// endpoint is bounded and paged rather than the flat, unpaginated shape
/// <see cref="CarriedInventoryQuery"/> uses — see the design spec's "The unbounded case is pending
/// deliveries, not carried items".
/// </summary>
public sealed record PendingDeliveriesQuery(CharacterId CharacterId, int? Limit) : IRequest<IReadOnlyList<ItemInstance>>;

public sealed class PendingDeliveriesHandler(
    IItemInstanceRepository repository,
    IWorldSettingsRepository settingsRepository,
    TimeProvider timeProvider)
    : IRequestHandler<PendingDeliveriesQuery, IReadOnlyList<ItemInstance>>
{
    public async ValueTask<IReadOnlyList<ItemInstance>> Handle(PendingDeliveriesQuery request, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken);
        var limit = request.Limit is > 0
            ? Math.Min(request.Limit.Value, settings.MaxPendingPageSize)
            : settings.MaxPendingPageSize;
        var now = timeProvider.GetUtcNow();

        var pending = await repository.FindPendingByRootCharacterAsync(
            request.CharacterId, limit, settings.MaxDeliveryAttempts, now, cancellationToken);

        // DeliveryAttempts is backend-owned and increments right here, on being served in this
        // payload — never conflated with Revision (the mod's LWW key) and never touched by the mod
        // itself. A row that reaches WorldSettings.MaxDeliveryAttempts this way simply stops matching
        // FindPendingByRootCharacterAsync's own filter on the next call, so there is no separate cap
        // check to get wrong here.
        //
        // RecordDeliveryAttempt is an atomic patch, deliberately not repository.Store(instance): the
        // Bridge retries GET /pending, so this handler routinely holds a copy of a row that a
        // concurrent ack has already cleared, and a whole-document write would put PendingSpawn back
        // — re-offering an item the player already holds. See that method's doc comment.
        foreach (var instance in pending)
        {
            repository.RecordDeliveryAttempt(instance, now);
        }

        await repository.SaveChangesAsync(cancellationToken);

        return pending;
    }
}
