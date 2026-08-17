namespace ELifeRPG.Shops.Api.Shops;

public sealed record AddListingRequestDto
{
    public required Guid ItemId { get; init; }

    public required decimal Price { get; init; }

    public required int Stock { get; init; }

    public required Guid ActingCharacterId { get; init; }

    public AddListingCommand ToCommand(Guid shopId) =>
        new(new ShopId(shopId), new ItemId(ItemId), Price, Stock, new CharacterId(ActingCharacterId));
}
