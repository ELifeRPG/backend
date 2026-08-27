using ELifeRPG.Items.Application.Common;

namespace ELifeRPG.Items.Application.Items;

/// <summary>
/// The whole catalog plus a version stamp. The Bridge fetches this at boot to learn what every
/// prefab means, and re-fetches when the version changes — it carries <c>itemId</c> on the snapshot
/// wire format precisely so prefab resolution never lands on the write path. See docs/bridge.md.
/// </summary>
public sealed record ItemCatalog(IReadOnlyList<Item> Items, long CatalogVersion);

public sealed record ItemsQuery : IRequest<ItemCatalog>;

public sealed class ItemsHandler(IItemRepository itemRepository) : IRequestHandler<ItemsQuery, ItemCatalog>
{
    public async ValueTask<ItemCatalog> Handle(ItemsQuery request, CancellationToken cancellationToken)
    {
        var items = await itemRepository.FindAllAsync(cancellationToken);
        var version = await itemRepository.GetCatalogVersionAsync(cancellationToken);
        return new ItemCatalog(items, version);
    }
}
