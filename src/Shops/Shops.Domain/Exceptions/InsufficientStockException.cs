namespace ELifeRPG.Shops.Domain.Exceptions;

/// <summary>Thrown by ShopListing.Purchase when the requested quantity exceeds current stock.</summary>
public sealed class InsufficientStockException(string message) : InvalidOperationException(message);
