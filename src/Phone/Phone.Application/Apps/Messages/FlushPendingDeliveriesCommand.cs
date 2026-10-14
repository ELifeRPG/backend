using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public sealed record FlushPendingDeliveriesResult(int Delivered, int StillPending);

/// <summary>
/// Delivers whatever piled up while a phone was powered off or had the Messages app uninstalled.
/// Fired after powering on and after installing Messages, which are exactly the two moments a number
/// becomes reachable again.
///
/// Safe to run at any time: a phone that is still unreachable simply leaves everything queued, and
/// each delivery is removed from the queue in the same commit that appends it to the thread.
/// </summary>
public sealed record FlushPendingDeliveriesCommand(PhoneDeviceId PhoneId) : IRequest<FlushPendingDeliveriesResult>;

public sealed class FlushPendingDeliveriesHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository,
    IPhoneNotificationRepository notificationRepository,
    IMediator mediator)
    : IRequestHandler<FlushPendingDeliveriesCommand, FlushPendingDeliveriesResult>
{
    public async ValueTask<FlushPendingDeliveriesResult> Handle(FlushPendingDeliveriesCommand request, CancellationToken cancellationToken)
    {
        var pending = await threadRepository.FindPendingForPhoneAsync(request.PhoneId, cancellationToken);
        if (pending.Count == 0)
        {
            return new FlushPendingDeliveriesResult(0, 0);
        }

        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);

        // A suspended phone keeps its backlog rather than losing it — the lock stops delivery, and a
        // restore is meant to hand the number back whole.
        if (phone is null || phone.Status != PhoneStatus.Active || !phone.IsPoweredOn || !phone.HasApp(AppKey.Messages))
        {
            return new FlushPendingDeliveriesResult(0, pending.Count);
        }

        var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);

        // Accumulated across the whole flush and published once below, not per delivery: several
        // pending messages for this one phone must be fitted against the cap together, and
        // PhoneNotifications.PublishAsync only sees what a single commit is about to add if it is
        // handed the whole batch at once — see its own doc comment.
        var notifications = new List<PhoneNotification>();

        foreach (var delivery in pending)
        {
            var thread = await MessageThreads.FindOrStartAsync(
                threadRepository, phone.Id, phone.Number, delivery.Participants,
                settings.PhoneMaxGroupParticipants, cancellationToken);

            threadRepository.Append(
                thread.Id,
                thread.RecordInbound(
                    delivery.MessageId, delivery.From, delivery.Body, delivery.SentAt, settings.PhoneThreadMessageLimit));

            threadRepository.DeletePending(delivery.Id);

            notifications.Add(PhoneNotification.Create(
                phone.Id,
                AppKey.Messages,
                category: "message.received",
                groupKey: thread.Id.Value.ToString(),
                title: delivery.From.Value,
                body: delivery.Body,
                payload: new Dictionary<string, string> { ["threadId"] = thread.Id.Value.ToString(), ["messageId"] = delivery.MessageId.Value.ToString() },
                occurredAt: delivery.SentAt));
        }

        await PhoneNotifications.PublishAsync(
            notificationRepository, phone.Id, notifications, settings.PhoneNotificationLimit, cancellationToken);

        // One commit for the appends, the dequeues and the notifications together, so a message can
        // never be both delivered and still waiting — nor dropped without ever arriving.
        await threadRepository.SaveChangesAsync(cancellationToken);

        return new FlushPendingDeliveriesResult(pending.Count, 0);
    }
}
