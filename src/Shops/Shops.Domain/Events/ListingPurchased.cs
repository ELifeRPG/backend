namespace ELifeRPG.Shops.Domain.Events;

public sealed record ListingPurchased(ShopListingId Id, int Quantity, int NewStock);
