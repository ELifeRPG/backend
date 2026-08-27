using ELifeRPG.Items.Domain.Events;

namespace ELifeRPG.Items.Application.Common;

public interface IItemRepository
{
    ValueTask<Item?> FindByIdAsync(ItemId itemId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Item>> FindAllAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Item>> FindByIdsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves catalog entries by prefab class name in one round trip. Bulk import needs this to be
    /// idempotent without an id, and it is only unambiguous because PrefabClassName is unique.
    /// </summary>
    ValueTask<IReadOnlyList<Item>> FindByPrefabClassNamesAsync(IReadOnlyList<string> prefabClassNames, CancellationToken cancellationToken);

    /// <summary>
    /// A number that changes whenever the catalog changes, so the Bridge can cheaply decide whether
    /// to re-fetch. Opaque and monotonic; carries no meaning beyond "different means stale".
    /// </summary>
    ValueTask<long> GetCatalogVersionAsync(CancellationToken cancellationToken);

    void StartStream(Item item, ItemCreated domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
