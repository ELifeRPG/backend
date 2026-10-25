using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Apps.Phone;
using ELifeRPG.Phone.Api.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace ELifeRPG.Phone.Api;

/// <summary>
/// Pushes live thread updates after a mutating endpoint's mediator call succeeds. Lives in Phone.Api
/// rather than Phone.Application for the same reason ShopsHubNotifier does: a Mediator handler can
/// not depend on IHubContext, because *.Application references only its own *.Domain
/// (ARCHITECTURE.md §9e), and SignalR types belong beside the hub.
/// </summary>
public sealed class PhoneHubNotifier(IHubContext<PhoneHub> hubContext)
{
    public Task NotifyMessageReceivedAsync(Guid phoneId, Guid threadId, MessageDto message, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(phoneId))
            .SendAsync("MessageReceived", new { phoneId, threadId, message }, cancellationToken);

    public Task NotifyThreadUpdatedAsync(Guid phoneId, MessageThreadSummaryDto thread, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(phoneId))
            .SendAsync("ThreadUpdated", new { phoneId, thread }, cancellationToken);

    /// <summary>
    /// The dialer's three. New event names on the existing per-phone group and nothing else — the hub
    /// is keyed by phone rather than by app precisely so an app costs event names, not subscription
    /// mechanics.
    ///
    /// Ringing is the one place in this module where the hub genuinely beats the poll: it is the
    /// difference between a phone ringing now and ringing on the next tick. It is still not the
    /// source of truth, so a client that misses the frame finds the call on
    /// <c>GET .../apps/phone/calls/ringing</c> anyway — and Reforger, which cannot hold a hub
    /// connection at all, only ever finds it there.
    /// </summary>
    public Task NotifyIncomingCallAsync(Guid phoneId, PhoneCallDto call, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(phoneId))
            .SendAsync("IncomingCall", new { phoneId, call }, cancellationToken);

    /// <inheritdoc cref="NotifyIncomingCallAsync"/>
    public Task NotifyCallStateChangedAsync(Guid phoneId, PhoneCallDto call, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(phoneId))
            .SendAsync("CallStateChanged", new { phoneId, call }, cancellationToken);

    /// <summary>
    /// Pushed from the send path only, never from the power-on flush: the flush pushes nothing over
    /// this hub today, and wiring it would mean threading a result through
    /// FlushPendingDeliveriesResult, SetPhonePowerResult and the power endpoint to feed a hub that is
    /// explicitly never the source of truth, for a phone that is about to GET /notifications as part
    /// of booting regardless.
    /// </summary>
    public Task NotifyNotificationPostedAsync(Guid phoneId, PhoneNotificationDto notification, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(phoneId))
            .SendAsync("NotificationPosted", new { phoneId, notification }, cancellationToken);
}
