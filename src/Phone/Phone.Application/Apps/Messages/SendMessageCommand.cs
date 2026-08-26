using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public union SendMessageResult(
    SendMessageResult.Sent,
    SendMessageResult.EmptyBody,
    SendMessageResult.BodyTooLong,
    SendMessageResult.NoRecipients,
    SendMessageResult.TooManyRecipients,
    SendMessageResult.RateLimited,
    SendMessageResult.AccessDenied)
{
    /// <summary>
    /// <paramref name="UndeliverableRecipients"/> is what the sender may legitimately learn: numbers
    /// that do not exist, or are suspended or retired — what a real network would report. Blocking
    /// is deliberately absent, because a blocked sender sees a delivered message.
    ///
    /// <paramref name="Deliveries"/> is not for the sender at all. It exists so the Api layer can
    /// push to exactly the SIMs that actually received an append, without re-deriving delivery — and
    /// without ever pushing to a blocked or queued recipient. The response DTO does not expose it.
    /// </summary>
    public record Sent(
        MessageThreadId ThreadId,
        MessageId MessageId,
        PhoneNumber From,
        DateTimeOffset SentAt,
        IReadOnlyList<PhoneNumber> UndeliverableRecipients,
        IReadOnlyList<MessageDelivery> Deliveries);

    public record EmptyBody;

    public record BodyTooLong(int MaxLength);

    public record NoRecipients;

    public record TooManyRecipients(int MaxParticipants);

    public record RateLimited(int PerMinuteLimit);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>One append that actually landed, for the Api layer's live push.</summary>
public sealed record MessageDelivery(SimCardId SimCardId, MessageThreadId ThreadId);

public sealed record SendMessageCommand(
    SimCardId SimCardId,
    CharacterId ActingCharacterId,
    IReadOnlyList<PhoneNumber> To,
    string Body) : IRequest<SendMessageResult>;

/// <summary>
/// Fans a message out across the sender's thread and every reachable recipient's. All appends run on
/// the shared <c>IPhoneSession</c>, so one commit covers the lot — a message sitting in the sender's
/// history but in nobody's inbox is the outcome this flow exists to prevent.
/// </summary>
public sealed class SendMessageHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IMessageThreadRepository threadRepository,
    ISimSendWindowRepository sendWindowRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<SendMessageCommand, SendMessageResult>
{
    public async ValueTask<SendMessageResult> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Messages,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new SendMessageResult.AccessDenied(access);
        }

        var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return new SendMessageResult.EmptyBody();
        }

        if (request.Body.Length > settings.SmsMaxBodyLength)
        {
            return new SendMessageResult.BodyTooLong(settings.SmsMaxBodyLength);
        }

        var sender = granted.SimCard;

        // Addressing yourself alongside others is fine and simply drops out; addressing only
        // yourself leaves nobody to send to.
        var recipients = request.To
            .Where(number => number != sender.Number)
            .Distinct()
            .ToList();

        if (recipients.Count == 0)
        {
            return new SendMessageResult.NoRecipients();
        }

        if (recipients.Count > granted.Model.MaxGroupParticipants)
        {
            return new SendMessageResult.TooManyRecipients(granted.Model.MaxGroupParticipants);
        }

        var now = timeProvider.GetUtcNow();
        if (!await TryConsumeQuotaAsync(request.SimCardId, settings.SmsPerMinutePerSim, now, cancellationToken))
        {
            return new SendMessageResult.RateLimited(settings.SmsPerMinutePerSim);
        }

        var messageId = new MessageId(Guid.NewGuid());
        var undeliverable = new List<PhoneNumber>();
        var deliveries = new List<MessageDelivery>();

        foreach (var recipientNumber in recipients)
        {
            var recipientSim = await simCardRepository.FindByNumberAsync(recipientNumber, cancellationToken);

            // A number nobody holds, or one the state has locked or retired, is simply unreachable.
            // Suspended never queues: holding the message for a later restore would turn an
            // enforcement action into a delay.
            if (recipientSim is null || recipientSim.Status != SimCardStatus.Active)
            {
                undeliverable.Add(recipientNumber);
                continue;
            }

            if (recipientSim.IsBlocked(sender.Number))
            {
                // Dropped in silence, and not reported back — the sender must not be able to probe
                // whether they have been blocked.
                continue;
            }

            // The recipient's thread is "everyone else on this conversation", which from their side
            // means the sender plus the other recipients.
            var recipientParticipants = recipients
                .Where(number => number != recipientNumber)
                .Append(sender.Number)
                .ToList();

            var recipientDevice = recipientSim.InstalledIn is { } deviceId
                ? await deviceRepository.FindByIdAsync(deviceId, cancellationToken)
                : null;

            if (recipientDevice is null || !recipientDevice.IsPoweredOn)
            {
                threadRepository.StorePending(new PendingDelivery
                {
                    Id = Guid.NewGuid(),
                    RecipientSimCardId = recipientSim.Id,
                    MessageId = messageId,
                    From = sender.Number,
                    Participants = recipientParticipants,
                    Body = request.Body,
                    SentAt = now,
                });
                continue;
            }

            var recipientModel = await modelRepository.FindByIdAsync(recipientDevice.ModelId, cancellationToken);
            if (recipientModel is null || !recipientDevice.HasApp(AppKey.Messages))
            {
                // No Messages app installed on the handset the SIM is in: hold it, so installing the
                // app later delivers the backlog rather than losing it.
                threadRepository.StorePending(new PendingDelivery
                {
                    Id = Guid.NewGuid(),
                    RecipientSimCardId = recipientSim.Id,
                    MessageId = messageId,
                    From = sender.Number,
                    Participants = recipientParticipants,
                    Body = request.Body,
                    SentAt = now,
                });
                continue;
            }

            var recipientThread = await MessageThreads.FindOrStartAsync(
                threadRepository, recipientSim.Id, recipientSim.Number, recipientParticipants, recipientModel, cancellationToken);

            threadRepository.Append(
                recipientThread.Id,
                recipientThread.RecordInbound(messageId, sender.Number, request.Body, now, recipientModel));

            deliveries.Add(new MessageDelivery(recipientSim.Id, recipientThread.Id));
        }

        // Appended regardless of what happened downstream: texting a dead, blocked or suspended
        // number still reads as sent from the sender's side, exactly like SMS.
        var senderThread = await MessageThreads.FindOrStartAsync(
            threadRepository, sender.Id, sender.Number, recipients, granted.Model, cancellationToken);

        threadRepository.Append(
            senderThread.Id,
            senderThread.RecordOutbound(messageId, sender.Number, request.Body, now, granted.Model));

        await threadRepository.SaveChangesAsync(cancellationToken);

        return new SendMessageResult.Sent(senderThread.Id, messageId, sender.Number, now, undeliverable, deliveries);
    }

    /// <summary>
    /// A fixed window rather than a rolling one: it costs a single document and one round trip, and
    /// the worst case it allows — a burst spanning a window boundary — is still bounded by twice the
    /// limit, which is well within what a throttle is for here.
    /// </summary>
    private async ValueTask<bool> TryConsumeQuotaAsync(
        SimCardId simCardId, int perMinuteLimit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var window = await sendWindowRepository.FindAsync(simCardId, cancellationToken)
            ?? new SimSendWindow { Id = simCardId, WindowStartedAt = now, Count = 0 };

        if (now - window.WindowStartedAt >= TimeSpan.FromMinutes(1))
        {
            window.WindowStartedAt = now;
            window.Count = 0;
        }

        if (window.Count >= perMinuteLimit)
        {
            return false;
        }

        window.Count++;
        await sendWindowRepository.StoreAsync(window, cancellationToken);
        return true;
    }
}

internal static class MessageThreads
{
    /// <summary>
    /// Threads are keyed by (SIM, participant set), so a conversation is found rather than chosen —
    /// there is no group object to create, exactly like SMS.
    /// </summary>
    public static async ValueTask<MessageThread> FindOrStartAsync(
        IMessageThreadRepository threadRepository,
        SimCardId ownerSimCardId,
        PhoneNumber ownerNumber,
        IReadOnlyList<PhoneNumber> participants,
        PhoneModel model,
        CancellationToken cancellationToken)
    {
        var started = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), ownerSimCardId, ownerNumber, participants, model);

        if (await threadRepository.FindByKeyAsync(ownerSimCardId, started.ThreadKey, cancellationToken) is { } existing)
        {
            return existing;
        }

        var thread = MessageThread.Create(started);
        threadRepository.StartStream(thread, started);
        return thread;
    }
}
