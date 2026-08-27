using ELifeRPG.Items.Application.Common;

namespace ELifeRPG.Items.Application.Items;

/// <summary>
/// Batched existence-and-rules lookup for a set of catalog ids — the cross-module contract the World
/// module dispatches into while applying a snapshot. Batched on purpose: resolving ids one at a time
/// would put N cross-module round trips on the hot write path. Ids with no catalog entry are simply
/// absent from the result, which is how "uncatalogued prefabs are not persisted" is enforced.
/// </summary>
public sealed record ItemCatalogEntry(ItemId ItemId, string PrefabClassName, ItemPersistence Persistence);

public sealed record ItemCatalogEntriesQuery(IReadOnlyList<ItemId> ItemIds) : IRequest<IReadOnlyDictionary<ItemId, ItemCatalogEntry>>;

public sealed class ItemCatalogEntriesHandler(IItemRepository itemRepository)
    : IRequestHandler<ItemCatalogEntriesQuery, IReadOnlyDictionary<ItemId, ItemCatalogEntry>>
{
    public async ValueTask<IReadOnlyDictionary<ItemId, ItemCatalogEntry>> Handle(
        ItemCatalogEntriesQuery request,
        CancellationToken cancellationToken)
    {
        if (request.ItemIds.Count == 0)
        {
            return new Dictionary<ItemId, ItemCatalogEntry>();
        }

        var items = await itemRepository.FindByIdsAsync(request.ItemIds, cancellationToken);
        return items.ToDictionary(
            x => x.Id,
            x => new ItemCatalogEntry(x.Id, x.PrefabClassName, x.Persistence));
    }
}
