namespace ELifeRPG.Shops.Domain.Events;

public sealed record ListingUpdated(ShopListingId Id, decimal Price, int Stock);
