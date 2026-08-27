using ELifeRPG.Items.Application.Items;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// Resolves a catalog item's <c>PrefabClassName</c> for the grant path. Kept as this narrow an
/// abstraction, rather than exposing <c>ItemCatalogEntriesQuery</c> itself past this file, so that
/// <c>MartenItemInstanceRepository</c> (World.Infrastructure) never needs its own dependency on
/// Items — only this interface, which lives in World.Application alongside its one implementation.
/// See World.Application.csproj's comment on the Items.Application reference (ARCHITECTURE.md §9e's
/// Application-&gt;Application exception).
/// </summary>
public interface IItemCatalogResolver
{
    /// <summary>Null if <paramref name="itemId"/> has no catalog entry.</summary>
    ValueTask<string?> ResolvePrefabClassNameAsync(ItemId itemId, CancellationToken cancellationToken);

    /// <summary>
    /// The batched form: one dispatch for however many distinct ids a caller needs, with ids that have
    /// no catalog entry simply absent from the result. Callers that resolve a <i>set</i> must use this
    /// rather than looping over the single-id overload — the underlying
    /// <c>ItemCatalogEntriesQuery</c> is already batched, and dispatching it once per id opens Items'
    /// own scoped session that many times, from inside an open World session
    /// (<c>AcknowledgeSpawnsHandler</c> was doing exactly that: 500 declared children of the same item
    /// meant 500 round trips, against a design that mandates "one batched catalog check").
    /// </summary>
    ValueTask<IReadOnlyDictionary<ItemId, string>> ResolvePrefabClassNamesAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken);
}

/// <summary>
/// The one place in this module that references Items.Application's batched
/// <see cref="ItemCatalogEntriesQuery"/> contract, dispatched via <see cref="IMediator"/> rather than
/// any direct dependency on Items.Infrastructure.
/// </summary>
public sealed class ItemCatalogResolver(IMediator mediator) : IItemCatalogResolver
{
    public async ValueTask<string?> ResolvePrefabClassNameAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        var entries = await mediator.Send(new ItemCatalogEntriesQuery([itemId]), cancellationToken);
        return entries.TryGetValue(itemId, out var entry) ? entry.PrefabClassName : null;
    }

    public async ValueTask<IReadOnlyDictionary<ItemId, string>> ResolvePrefabClassNamesAsync(
        IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, string>();
        }

        var distinct = itemIds.Distinct().ToList();
        var entries = await mediator.Send(new ItemCatalogEntriesQuery(distinct), cancellationToken);
        return entries.ToDictionary(x => x.Key, x => x.Value.PrefabClassName);
    }
}
