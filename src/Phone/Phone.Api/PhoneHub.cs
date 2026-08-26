using ELifeRPG.Phone.Infrastructure.Common;
using Marten;
using Microsoft.AspNetCore.SignalR;

namespace ELifeRPG.Phone.Api;

/// <summary>
/// Groups are keyed by SIM rather than by device or by app: that is where threads live, so a
/// subscription survives the SIM moving to another handset, and one subscription carries every app's
/// events — adding an app needs new event names, not new subscription mechanics.
///
/// Unlike ShopsHub, subscribing authorizes. Shops are hive-public; message threads are not, so it is
/// not enough that the connection cleared the hub's own policy — it must also own the SIM.
///
/// Same rule as Shops otherwise: the hub is a delivery convenience, never the source of truth.
/// Clients re-fetch on reconnect.
/// </summary>
public sealed class PhoneHub(IPhoneStore phoneStore) : Hub
{
    public async Task SubscribeToSim(Guid simCardId, CancellationToken cancellationToken)
    {
        var characterId = ParseCharacterId(Context.GetHttpContext()?.Request.Query["characterId"]);
        if (characterId is null)
        {
            return;
        }

        await using var session = phoneStore.QuerySession();
        var sim = await session.LoadAsync<SimCard>(new SimCardId(simCardId), cancellationToken);

        // Silently no-op rather than throwing, matching ShopsHub's handling of an unknown shop: a
        // hub method has no useful error channel, and refusing to join is the whole effect needed.
        if (sim is null || sim.RegisteredTo.Value != characterId)
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(simCardId), cancellationToken);
    }

    public Task UnsubscribeFromSim(Guid simCardId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(simCardId));

    internal static string GroupName(Guid simCardId) => $"sim-{simCardId}";

    private static Guid? ParseCharacterId(string? raw) => Guid.TryParse(raw, out var parsed) ? parsed : null;
}
