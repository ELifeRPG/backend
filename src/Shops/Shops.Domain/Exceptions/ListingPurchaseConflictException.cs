namespace ELifeRPG.Shops.Domain.Exceptions;

/// <summary>
/// Thrown by IShopListingRepository.SaveChangesAsync when a pending ReserveStockAsync reservation
/// lost a race — another purchase already committed against this exact listing's stream between this
/// call's fetch and save. Raised by the repository (Shops.Infrastructure), not by ShopListing itself
/// — see MartenShopListingRepository.SaveChangesAsync.
/// </summary>
public sealed class ListingPurchaseConflictException(string message) : InvalidOperationException(message);
