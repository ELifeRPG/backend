using ELifeRPG.Phone.Infrastructure.Common;
using Marten;
using Microsoft.AspNetCore.SignalR;

namespace ELifeRPG.Phone.Api;

/// <summary>
/// Groups are keyed by phone rather than by app: that is where threads live, so one subscription
/// carries every app's events — adding an app needs new event names, not new subscription mechanics.
///
/// Unlike ShopsHub, subscribing authorizes. Shops are hive-public; message threads are not, so it is
/// not enough that the connection cleared the hub's own policy — the caller must own the phone.
/// Ownership, not the PIN: a live subscription is a standing grant rather than a single act, so it
/// is deliberately narrower than what PhoneAccessPolicy allows a borrower to do.
///
/// Same rule as Shops otherwise: the hub is a delivery convenience, never the source of truth.
/// Clients re-fetch on reconnect.
/// </summary>
public sealed class PhoneHub(IPhoneStore phoneStore) : Hub
{
    public async Task SubscribeToPhone(Guid phoneId, CancellationToken cancellationToken)
    {
        var characterId = ParseCharacterId(Context.GetHttpContext()?.Request.Query["characterId"]);
        if (characterId is null)
        {
            return;
        }

        await using var session = phoneStore.QuerySession();
        var phone = await session.LoadAsync<PhoneDevice>(new PhoneDeviceId(phoneId), cancellationToken);

        // Silently no-op rather than throwing, matching ShopsHub's handling of an unknown shop: a
        // hub method has no useful error channel, and refusing to join is the whole effect needed.
        if (phone is null || phone.RegisteredTo.Value != characterId)
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(phoneId), cancellationToken);
    }

    public Task UnsubscribeFromPhone(Guid phoneId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(phoneId));

    internal static string GroupName(Guid phoneId) => $"phone-{phoneId}";

    private static Guid? ParseCharacterId(string? raw) => Guid.TryParse(raw, out var parsed) ? parsed : null;
}
