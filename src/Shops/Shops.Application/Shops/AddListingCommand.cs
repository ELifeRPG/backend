using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain.Events;

namespace ELifeRPG.Shops.Application.Shops;

public union AddListingResult(
    AddListingResult.Added,
    AddListingResult.ShopNotFound,
    AddListingResult.ItemNotFound,
    AddListingResult.NotAuthorized,
    AddListingResult.InvalidPrice)
{
    public record Added(ShopListingId ListingId);

    public record ShopNotFound;

    public record ItemNotFound;

    public record NotAuthorized;

    /// <summary>Price was not strictly positive — see the validation note in AddListingHandler.</summary>
    public record InvalidPrice;
}

public sealed record AddListingCommand(ShopId ShopId, ItemId ItemId, decimal Price, int Stock, CharacterId ActingCharacterId) : IRequest<AddListingResult>;

public sealed class AddListingHandler(IShopRepository shopRepository, IShopListingRepository listingRepository, IMediator mediator)
    : IRequestHandler<AddListingCommand, AddListingResult>
{
    public async ValueTask<AddListingResult> Handle(AddListingCommand request, CancellationToken cancellationToken)
    {
        // Validated here rather than in ShopListing.Create: Create only applies a pre-built event
        // (matching every other aggregate in this codebase) and so has no invariant hook. The bound
        // is Banking's, not an arbitrary one — TransferOut's EnsurePositiveAmount rejects <= 0, so a
        // zero-priced listing would reserve stock and then fail to settle (ShopListing.UpdatePriceAndStock
        // guards the same bound on the update path). Caller-triggerable, hence a union case rather
        // than a propagating ArgumentOutOfRangeException — ARCHITECTURE.md §9e.
        if (request.Price <= 0)
        {
            return new AddListingResult.InvalidPrice();
        }

        var shop = await shopRepository.FindByIdAsync(request.ShopId, cancellationToken);
        if (shop is null)
        {
            return new AddListingResult.ShopNotFound();
        }

        if (!await ShopAuthorization.CanManageAsync(shop, request.ActingCharacterId, mediator, cancellationToken))
        {
            return new AddListingResult.NotAuthorized();
        }

        var itemLookup = await mediator.Send(new ItemLookupQuery(request.ItemId), cancellationToken);
        if (itemLookup is ItemLookupResult.NotFound)
        {
            return new AddListingResult.ItemNotFound();
        }

        var listingId = new ShopListingId(Guid.NewGuid());
        var domainEvent = new ListingCreated(listingId, request.ShopId, request.ItemId, request.Price, request.Stock);
        var listing = ShopListing.Create(domainEvent);

        listingRepository.StartStream(listing, domainEvent);
        await listingRepository.SaveChangesAsync(cancellationToken);

        return new AddListingResult.Added(listingId);
    }
}
