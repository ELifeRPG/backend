using ELifeRPG.Shops.Application.Common;

namespace ELifeRPG.Shops.Application.Shops;

public union UpdateListingResult(UpdateListingResult.Updated, UpdateListingResult.ShopNotFound, UpdateListingResult.ListingNotFound, UpdateListingResult.NotAuthorized)
{
    public record Updated;

    public record ShopNotFound;

    public record ListingNotFound;

    public record NotAuthorized;
}

public sealed record UpdateListingCommand(ShopId ShopId, ShopListingId ListingId, decimal Price, int Stock, CharacterId ActingCharacterId) : IRequest<UpdateListingResult>;

public sealed class UpdateListingHandler(IShopRepository shopRepository, IShopListingRepository listingRepository, IMediator mediator)
    : IRequestHandler<UpdateListingCommand, UpdateListingResult>
{
    public async ValueTask<UpdateListingResult> Handle(UpdateListingCommand request, CancellationToken cancellationToken)
    {
        var shop = await shopRepository.FindByIdAsync(request.ShopId, cancellationToken);
        if (shop is null)
        {
            return new UpdateListingResult.ShopNotFound();
        }

        if (!await ShopAuthorization.CanManageAsync(shop, request.ActingCharacterId, mediator, cancellationToken))
        {
            return new UpdateListingResult.NotAuthorized();
        }

        var listing = await listingRepository.FindByIdAsync(request.ListingId, cancellationToken);
        // A soft-removed listing is gone as far as callers are concerned — see the matching note in
        // PurchaseListingHandler.
        if (listing is null || listing.ShopId != request.ShopId || !listing.IsActive)
        {
            return new UpdateListingResult.ListingNotFound();
        }

        var domainEvent = listing.UpdatePriceAndStock(request.Price, request.Stock);
        listingRepository.Append(request.ListingId, domainEvent);
        await listingRepository.SaveChangesAsync(cancellationToken);

        return new UpdateListingResult.Updated();
    }
}
