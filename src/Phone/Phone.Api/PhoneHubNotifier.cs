using ELifeRPG.Phone.Api.Apps.Messages;
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
    public Task NotifyMessageReceivedAsync(Guid simCardId, Guid threadId, MessageDto message, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(simCardId))
            .SendAsync("MessageReceived", new { simCardId, threadId, message }, cancellationToken);

    public Task NotifyThreadUpdatedAsync(Guid simCardId, MessageThreadSummaryDto thread, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(PhoneHub.GroupName(simCardId))
            .SendAsync("ThreadUpdated", new { simCardId, thread }, cancellationToken);
}
