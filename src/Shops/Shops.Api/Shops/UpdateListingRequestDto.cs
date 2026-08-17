namespace ELifeRPG.Shops.Api.Shops;

public sealed record UpdateListingRequestDto
{
    public required decimal Price { get; init; }

    public required int Stock { get; init; }

    public required Guid ActingCharacterId { get; init; }

    public UpdateListingCommand ToCommand(Guid shopId, Guid listingId) =>
        new(new ShopId(shopId), new ShopListingId(listingId), Price, Stock, new CharacterId(ActingCharacterId));
}
