using Microsoft.AspNetCore.SignalR;

namespace ELifeRPG.Shops.Api;

/// <summary>
/// Pushes live listing updates after a mutating REST endpoint's mediator call succeeds. Lives in
/// Shops.Api, not Shops.Application, because a Mediator handler can't depend on IHubContext —
/// *.Application projects reference only their own module's *.Domain (ARCHITECTURE.md §9e), and
/// SignalR types belong in the Api layer alongside the hub itself.
/// </summary>
public sealed class ShopsHubNotifier(IHubContext<ShopsHub> hubContext)
{
    public Task NotifyListingChangedAsync(Guid shopId, Guid listingId, decimal price, int stock, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(ShopsHub.GroupName(shopId)).SendAsync("ListingChanged", new { shopId, listingId, price, stock }, cancellationToken);

    public Task NotifyListingRemovedAsync(Guid shopId, Guid listingId, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(ShopsHub.GroupName(shopId)).SendAsync("ListingRemoved", new { shopId, listingId }, cancellationToken);
}
