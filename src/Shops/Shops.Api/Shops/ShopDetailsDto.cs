namespace ELifeRPG.Shops.Api.Shops;

public sealed record ShopDetailsDto
{
    public required ShopDto Shop { get; init; }

    public required IReadOnlyList<ShopListingDto> Listings { get; init; }

    public static ShopDetailsDto Create(Shop shop, IReadOnlyList<ShopListing> listings) => new()
    {
        Shop = ShopDto.Create(shop),
        Listings = listings.Select(ShopListingDto.Create).ToList(),
    };
}
