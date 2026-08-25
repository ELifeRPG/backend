using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Infrastructure.Common;
using Marten;
using Microsoft.AspNetCore.SignalR;

namespace ELifeRPG.Shops.Api;

public sealed class ShopsHub(IShopsStore shopsStore) : Hub
{
    public async Task SubscribeToShop(Guid shopId, CancellationToken cancellationToken)
    {
        // Hive-wide as of the 2026-08-22 tenancy change: every shop is visible to every server, so
        // there is no cross-tenant leak to guard against when joining a shop's group. The connection
        // still had to authenticate and satisfy RequireAuthorization(ShopsWritePolicy) on the hub
        // mapping itself — this method only additionally checks that the shop actually exists.
        await using var session = shopsStore.QuerySession();
        var shop = await session.LoadAsync<Shop>(new ShopId(shopId), cancellationToken);
        if (shop is null)
        {
            // Same externally-visible behavior as GET /api/shops/{shopId} for a genuinely unknown
            // shop id — silently no-op rather than joining the group.
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(shopId), cancellationToken);
    }

    public Task UnsubscribeFromShop(Guid shopId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(shopId));

    internal static string GroupName(Guid shopId) => $"shop-{shopId}";
}
