namespace ELifeRPG.Shops.Api.Shops;

public sealed record ShopListingDto
{
    public required Guid ListingId { get; init; }

    public required Guid ItemId { get; init; }

    public required decimal Price { get; init; }

    public required int Stock { get; init; }

    public static ShopListingDto Create(ShopListing source) => new()
    {
        ListingId = source.Id.Value,
        ItemId = source.ItemId.Value,
        Price = source.Price,
        Stock = source.Stock,
    };
}
