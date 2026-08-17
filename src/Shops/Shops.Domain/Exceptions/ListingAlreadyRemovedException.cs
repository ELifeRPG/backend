namespace ELifeRPG.Shops.Domain.Exceptions;

public sealed class ListingAlreadyRemovedException(string message) : InvalidOperationException(message);
