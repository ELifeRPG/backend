using ELifeRPG.Items.Application.Common;

namespace ELifeRPG.Items.Application.Items;

/// <summary>
/// Doubles as both the GET /api/items/{id} backing query and the cross-module contract Shops
/// dispatches into to validate an ItemId — same shape as CharacterLookupQuery. See ARCHITECTURE.md §9e.
/// </summary>
public union ItemLookupResult(ItemLookupResult.Found, ItemLookupResult.NotFound)
{
    public record Found(Item Item);

    public record NotFound;
}

public sealed record ItemLookupQuery(ItemId ItemId) : IRequest<ItemLookupResult>;

public sealed class ItemLookupHandler(IItemRepository itemRepository) : IRequestHandler<ItemLookupQuery, ItemLookupResult>
{
    public async ValueTask<ItemLookupResult> Handle(ItemLookupQuery request, CancellationToken cancellationToken)
    {
        var item = await itemRepository.FindByIdAsync(request.ItemId, cancellationToken);
        return item is null ? new ItemLookupResult.NotFound() : new ItemLookupResult.Found(item);
    }
}
