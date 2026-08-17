namespace ELifeRPG.Shops.Api.Shops;

public sealed record PurchaseListingRequestDto
{
    public required int Quantity { get; init; }

    public required Guid BuyerCharacterId { get; init; }

    public required Guid BuyerBankAccountId { get; init; }

    public PurchaseListingCommand ToCommand(Guid shopId, Guid listingId) => new(
        new ShopId(shopId),
        new ShopListingId(listingId),
        Quantity,
        new CharacterId(BuyerCharacterId),
        new BankAccountId(BuyerBankAccountId));
}
