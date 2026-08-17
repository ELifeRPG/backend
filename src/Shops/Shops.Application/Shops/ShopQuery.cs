using ELifeRPG.Shops.Application.Common;

namespace ELifeRPG.Shops.Application.Shops;

public union ShopQueryResult(ShopQueryResult.Found, ShopQueryResult.NotFound)
{
    public record Found(Shop Shop, IReadOnlyList<ShopListing> Listings);

    public record NotFound;
}

public sealed record ShopQuery(ShopId ShopId) : IRequest<ShopQueryResult>;

public sealed class ShopHandler(IShopRepository shopRepository, IShopListingRepository listingRepository)
    : IRequestHandler<ShopQuery, ShopQueryResult>
{
    public async ValueTask<ShopQueryResult> Handle(ShopQuery request, CancellationToken cancellationToken)
    {
        var shop = await shopRepository.FindByIdAsync(request.ShopId, cancellationToken);
        if (shop is null)
        {
            return new ShopQueryResult.NotFound();
        }

        var listings = await listingRepository.FindByShopIdAsync(request.ShopId, cancellationToken);
        return new ShopQueryResult.Found(shop, listings.Where(x => x.IsActive).ToList());
    }
}
