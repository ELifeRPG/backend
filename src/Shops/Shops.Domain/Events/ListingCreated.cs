namespace ELifeRPG.Shops.Domain.Events;

public sealed record ListingCreated(ShopListingId Id, ShopId ShopId, ItemId ItemId, decimal Price, int Stock);
