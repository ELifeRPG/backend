using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Notifications;

/// <summary>
/// Publishing helper shared by every app that posts into the notification queue — today only
/// Messages, from <see cref="Apps.Messages.SendMessageCommand"/> and
/// <see cref="Apps.Messages.FlushPendingDeliveriesCommand"/>.
///
/// Enforcing the cap needs exactly one read of the phone's current queue per call, not one per
/// notification: an uncommitted <c>Store</c> never comes back from a later <c>Query</c> in the same
/// session, so counting per-notification against a queue that has not been re-read would under-count
/// whatever this same commit already added. Callers that may publish several notifications to one
/// phone in a single commit — a power-on flush delivering more than one queued message — must pass
/// them together in <paramref name="incoming"/> rather than calling this once per notification.
/// </summary>
internal static class PhoneNotifications
{
    public static async ValueTask PublishAsync(
        IPhoneNotificationRepository notifications,
        PhoneDeviceId phoneId,
        IReadOnlyList<PhoneNotification> incoming,
        int limit,
        CancellationToken cancellationToken)
    {
        if (incoming.Count == 0)
        {
            return;
        }

        var existing = await notifications.FindForPhoneAsync(phoneId, appKey: null, cancellationToken);
        var (toStore, toDelete) = PhoneNotification.Fit(existing, incoming, limit);

        // toStore also lists existing survivors, which are already persisted and need no action —
        // only the incoming ones that actually made it past the cap are new writes.
        var kept = toStore.Select(n => n.Id).ToHashSet();
        foreach (var notification in incoming.Where(n => kept.Contains(n.Id)))
        {
            notifications.Store(notification);
        }

        if (toDelete.Count > 0)
        {
            notifications.Delete(phoneId, toDelete);
        }
    }
}
