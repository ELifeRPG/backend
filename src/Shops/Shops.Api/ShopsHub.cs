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
        var clientId = Context.User?.FindFirst("client_id")?.Value;
        if (string.IsNullOrEmpty(clientId))
        {
            throw new HubException("No client_id claim on this connection; cannot resolve the current gameserver.");
        }

        await using var session = shopsStore.QuerySession(clientId);
        var shop = await session.LoadAsync<Shop>(new ShopId(shopId), cancellationToken);
        if (shop is null)
        {
            // Same externally-visible behavior as GET /api/shops/{shopId} for a shop belonging to
            // a different tenant (or a genuinely unknown id) — silently no-op rather than joining
            // the group, so the caller learns nothing about whether the shop exists elsewhere.
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(shopId), cancellationToken);
    }

    public Task UnsubscribeFromShop(Guid shopId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(shopId));

    internal static string GroupName(Guid shopId) => $"shop-{shopId}";
}
