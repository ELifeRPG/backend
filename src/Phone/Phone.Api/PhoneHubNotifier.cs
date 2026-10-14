using ELifeRPG.Phone.Api.Apps.Messages;
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
