using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Items.Domain.Exceptions;

namespace ELifeRPG.Items.Application.Items;

/// <summary>
/// One prefab to register. <see cref="DisplayName"/> falls back to the prefab class name so a bulk
/// import straight off a Reforger prefab dump needs no curation pass first — staff rename later.
/// </summary>
public sealed record BulkImportItem(
    string PrefabClassName,
    string? DisplayName = null,
    ItemPersistence Persistence = ItemPersistence.Despawns);

public sealed record BulkImportItemResult(string PrefabClassName, ItemId ItemId, bool Created);

public union BulkImportItemsResult(BulkImportItemsResult.Imported, BulkImportItemsResult.DuplicateInPayload)
{
    public record Imported(IReadOnlyList<BulkImportItemResult> Results);

    /// <summary>
    /// The payload itself names one prefab twice. Rejected wholesale rather than silently keeping
    /// the last one, because which of the two definitions won would otherwise be invisible.
    /// </summary>
    public record DuplicateInPayload(IReadOnlyList<string> PrefabClassNames);
}

/// <summary>
/// Idempotent on prefab class name: importing the same list twice creates nothing the second time
/// and returns the existing ids. This is what makes "uncatalogued prefabs are not persisted"
/// survivable — without a bulk path, the catalog starts empty and players lose everything they loot.
/// Existing entries are returned untouched, never updated; changing a definition is a separate
/// concern with its own audit trail.
/// </summary>
public sealed record BulkImportItemsCommand(IReadOnlyList<BulkImportItem> Items) : IRequest<BulkImportItemsResult>;

public sealed class BulkImportItemsHandler(IItemRepository itemRepository)
    : IRequestHandler<BulkImportItemsCommand, BulkImportItemsResult>
{
    public async ValueTask<BulkImportItemsResult> Handle(BulkImportItemsCommand request, CancellationToken cancellationToken)
    {
        var duplicates = request.Items
            .GroupBy(x => x.PrefabClassName, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            return new BulkImportItemsResult.DuplicateInPayload(duplicates);
        }

        if (request.Items.Count == 0)
        {
            return new BulkImportItemsResult.Imported([]);
        }

        var prefabClassNames = request.Items.Select(x => x.PrefabClassName).ToList();
        var existing = (await itemRepository.FindByPrefabClassNamesAsync(prefabClassNames, cancellationToken))
            .ToDictionary(x => x.PrefabClassName, StringComparer.Ordinal);

        var results = new List<BulkImportItemResult>(request.Items.Count);
        var created = false;

        foreach (var candidate in request.Items)
        {
            if (existing.TryGetValue(candidate.PrefabClassName, out var alreadyKnown))
            {
                results.Add(new BulkImportItemResult(candidate.PrefabClassName, alreadyKnown.Id, Created: false));
                continue;
            }

            var itemId = new ItemId(Guid.NewGuid());
            var domainEvent = new ItemCreated(
                itemId,
                string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.PrefabClassName : candidate.DisplayName,
                candidate.PrefabClassName,
                candidate.Persistence);

            itemRepository.StartStream(Item.Create(domainEvent), domainEvent);
            results.Add(new BulkImportItemResult(candidate.PrefabClassName, itemId, Created: true));
            created = true;
        }

        if (created)
        {
            // One transaction for the whole import: a partially-applied catalog would leave the
            // Bridge persisting some prefabs and dropping others with no way to tell which.
            try
            {
                await itemRepository.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicatePrefabClassNameException exception)
            {
                // Lost the race against a concurrent import of the same prefab. The winner's entry
                // is the correct one, so surface it as a payload conflict for the caller to retry.
                return new BulkImportItemsResult.DuplicateInPayload([exception.PrefabClassName]);
            }
        }

        return new BulkImportItemsResult.Imported(results);
    }
}
