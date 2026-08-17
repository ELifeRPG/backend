using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Shops.Infrastructure.Common;

public sealed partial class ShopListingProjection : SingleStreamProjection<ShopListing, ShopListingId>
{
    public static ShopListing Create(ListingCreated domainEvent) => ShopListing.Create(domainEvent);

    public void Apply(ShopListing listing, ListingUpdated domainEvent) => listing.Apply(domainEvent);

    public void Apply(ShopListing listing, ListingPurchased domainEvent) => listing.Apply(domainEvent);

    public void Apply(ShopListing listing, ListingRemoved domainEvent) => listing.Apply(domainEvent);
}
