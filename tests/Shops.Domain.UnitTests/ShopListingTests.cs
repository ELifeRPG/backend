using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.Shops.Domain.Exceptions;
using Xunit;

namespace ELifeRPG.Shops.Domain.UnitTests;

public class ShopListingTests
{
    private static ShopListing CreateListing(int stock = 10, decimal price = 5m) =>
        ShopListing.Create(new ListingCreated(new ShopListingId(Guid.NewGuid()), new ShopId(Guid.NewGuid()), new ItemId(Guid.NewGuid()), price, stock));

    [Fact]
    public void Create_SetsAllFieldsAndActivatesListing()
    {
        var listing = CreateListing(stock: 10, price: 5m);

        Assert.Equal(10, listing.Stock);
        Assert.Equal(5m, listing.Price);
        Assert.True(listing.IsActive);
    }

    [Fact]
    public void UpdatePriceAndStock_WithValidValues_UpdatesBoth()
    {
        var listing = CreateListing();

        var domainEvent = listing.UpdatePriceAndStock(7.5m, 20);

        Assert.Equal(7.5m, listing.Price);
        Assert.Equal(20, listing.Stock);
        Assert.Equal(7.5m, domainEvent.Price);
        Assert.Equal(20, domainEvent.Stock);
    }

    [Fact]
    public void UpdatePriceAndStock_WithNegativePrice_ThrowsArgumentOutOfRange()
    {
        var listing = CreateListing();

        Assert.Throws<ArgumentOutOfRangeException>(() => listing.UpdatePriceAndStock(-1m, 5));
    }

    [Fact]
    public void UpdatePriceAndStock_WithZeroPrice_ThrowsArgumentOutOfRange()
    {
        // Zero is rejected too, not just negatives: Banking's TransferOut requires a strictly
        // positive amount, so a free listing could reserve stock it can never settle payment for.
        var listing = CreateListing();

        Assert.Throws<ArgumentOutOfRangeException>(() => listing.UpdatePriceAndStock(0m, 5));
    }

    [Fact]
    public void UpdatePriceAndStock_WithZeroStock_IsAllowed()
    {
        var listing = CreateListing(stock: 10);

        listing.UpdatePriceAndStock(5m, 0);

        Assert.Equal(0, listing.Stock);
    }

    [Fact]
    public void UpdatePriceAndStock_WithNegativeStock_ThrowsArgumentOutOfRange()
    {
        var listing = CreateListing();

        Assert.Throws<ArgumentOutOfRangeException>(() => listing.UpdatePriceAndStock(1m, -5));
    }

    [Fact]
    public void Purchase_WithSufficientStock_DecrementsStock()
    {
        var listing = CreateListing(stock: 10);

        var domainEvent = listing.Purchase(3);

        Assert.Equal(7, listing.Stock);
        Assert.Equal(3, domainEvent.Quantity);
        Assert.Equal(7, domainEvent.NewStock);
    }

    [Fact]
    public void Purchase_WithInsufficientStock_ThrowsInsufficientStock()
    {
        var listing = CreateListing(stock: 2);

        Assert.Throws<InsufficientStockException>(() => listing.Purchase(3));
    }

    [Fact]
    public void Remove_WhenActive_SetsIsActiveFalse()
    {
        var listing = CreateListing();

        listing.Remove();

        Assert.False(listing.IsActive);
    }

    [Fact]
    public void Remove_WhenAlreadyRemoved_ThrowsListingAlreadyRemoved()
    {
        var listing = CreateListing();
        listing.Remove();

        Assert.Throws<ListingAlreadyRemovedException>(() => listing.Remove());
    }

    [Fact]
    public void Apply_ReplayingCreatedThenPurchased_ResultsInDecrementedStock()
    {
        var listingId = new ShopListingId(Guid.NewGuid());
        var listing = new ShopListing();

        listing.Apply(new ListingCreated(listingId, new ShopId(Guid.NewGuid()), new ItemId(Guid.NewGuid()), 5m, 10));
        listing.Apply(new ListingPurchased(listingId, 4, 6));

        Assert.Equal(6, listing.Stock);
    }
}
