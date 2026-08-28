using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.Shops.Domain.Exceptions;

namespace ELifeRPG.Shops.Domain;

public class ShopListing
{
    public ShopListingId Id { get; private set; }

    public ShopId ShopId { get; private set; }

    public ItemId ItemId { get; private set; }

    public decimal Price { get; private set; }

    public int Stock { get; private set; }

    public bool IsActive { get; private set; }

    public static ShopListing Create(ListingCreated domainEvent)
    {
        var listing = new ShopListing();
        listing.Apply(domainEvent);
        return listing;
    }

    public ListingUpdated UpdatePriceAndStock(decimal price, int stock)
    {
        // Strictly positive, not merely non-negative: a zero (or negative) price would reach
        // Banking's TransferOut via PurchaseListingHandler, whose EnsurePositiveAmount guard rejects
        // anything <= 0 — leaving a purchase that has already reserved stock unable to settle.
        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be greater than zero.");
        }

        if (stock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stock), "Stock must be zero or greater.");
        }

        var domainEvent = new ListingUpdated(Id, price, stock);
        Apply(domainEvent);
        return domainEvent;
    }

    public ListingPurchased Purchase(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (quantity > Stock)
        {
            throw new InsufficientStockException("Not enough stock to fulfil this purchase.");
        }

        var domainEvent = new ListingPurchased(Id, quantity, Stock - quantity);
        Apply(domainEvent);
        return domainEvent;
    }

    public ListingRemoved Remove()
    {
        if (!IsActive)
        {
            throw new ListingAlreadyRemovedException("Listing has already been removed.");
        }

        var domainEvent = new ListingRemoved(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(ListingCreated domainEvent)
    {
        Id = domainEvent.Id;
        ShopId = domainEvent.ShopId;
        ItemId = domainEvent.ItemId;
        Price = domainEvent.Price;
        Stock = domainEvent.Stock;
        IsActive = true;
    }

    public void Apply(ListingUpdated domainEvent)
    {
        Price = domainEvent.Price;
        Stock = domainEvent.Stock;
    }

    public void Apply(ListingPurchased domainEvent)
    {
        Stock = domainEvent.NewStock;
    }

    public void Apply(ListingRemoved domainEvent)
    {
        IsActive = false;
    }
}
