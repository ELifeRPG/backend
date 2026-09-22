using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public union ThreadsResult(ThreadsResult.Threads, ThreadsResult.AccessDenied)
{
    public record Threads(IReadOnlyList<MessageThread> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ThreadsQuery(PhoneDeviceId PhoneId) : IRequest<ThreadsResult>;

public sealed class ThreadsHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadsQuery, ThreadsResult>
{
    public async ValueTask<ThreadsResult> Handle(ThreadsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        return access is PhoneAccessResult.Granted
            ? new ThreadsResult.Threads(await threadRepository.FindByPhoneAsync(request.PhoneId, cancellationToken))
            : new ThreadsResult.AccessDenied(access);
    }
}

public union ThreadResult(ThreadResult.Found, ThreadResult.NotFound, ThreadResult.AccessDenied)
{
    public record Found(MessageThread Thread);

    public record NotFound;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ThreadQuery(PhoneDeviceId PhoneId, MessageThreadId ThreadId)
    : IRequest<ThreadResult>;

public sealed class ThreadHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadQuery, ThreadResult>
{
    public async ValueTask<ThreadResult> Handle(ThreadQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new ThreadResult.AccessDenied(access);
        }

        var thread = await threadRepository.FindByIdAsync(request.ThreadId, cancellationToken);

        // A thread belonging to a different phone reads as absent rather than forbidden: whether
        // some other number holds a given thread id is not this caller's business.
        return thread is null || thread.OwnerPhoneId != request.PhoneId
            ? new ThreadResult.NotFound()
            : new ThreadResult.Found(thread);
    }
}

/// <summary>
/// One thread's metadata paired with just the messages the caller has not retrieved yet. The thread
/// itself is a Marten-projected aggregate with private setters, so "the same thread carrying fewer
/// messages" cannot be expressed as a copy of it — and should not be: a poll answers "what is new",
/// which is a different question from "what does this thread hold".
/// </summary>
public sealed record MessageThreadUpdate(MessageThread Thread, IReadOnlyList<Message> NewMessages);

public union MessageUpdatesResult(MessageUpdatesResult.Updates, MessageUpdatesResult.AccessDenied)
{
    /// <summary>
    /// Only threads with at least one message above their own <see cref="MessageThread.RetrievedThrough"/>
    /// are reported. There is no cursor to hand back here: retrieval is a server-tracked watermark
    /// per thread now, advanced explicitly through <see cref="AckMessageUpdatesCommand"/> — polling
    /// itself moves nothing.
    /// </summary>
    public record Updates(IReadOnlyList<MessageThreadUpdate> Threads);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// The polling counterpart to PhoneHub, for clients that cannot hold a socket: ArmA Reforger has no
/// SignalR client, so the Bridge asks what changed rather than being told. Like the hub, this is a
/// delivery convenience and never the source of truth — retention trimming (see MessageThread.Append)
/// can evict a message before a slow poller sees it, and ThreadQuery remains the authority.
///
/// Polling never marks anything read *or* retrieved: <see cref="MarkThreadReadCommand"/> is the only
/// thing that clears <see cref="MessageThread.UnreadCount"/>, and only
/// <see cref="AckMessageUpdatesCommand"/> advances <see cref="MessageThread.RetrievedThrough"/>.
/// </summary>
public sealed record MessageUpdatesQuery(PhoneDeviceId PhoneId) : IRequest<MessageUpdatesResult>;

public sealed class MessageUpdatesHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<MessageUpdatesQuery, MessageUpdatesResult>
{
    public async ValueTask<MessageUpdatesResult> Handle(MessageUpdatesQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new MessageUpdatesResult.AccessDenied(access);
        }

        var threads = await threadRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);

        // Filtered in memory rather than in the query: FindByPhoneAsync already loads this phone's
        // whole set for ThreadsHandler, and a phone holds few enough threads for that to be the
        // simpler trade.
        var changed = threads
            .Select(thread => new MessageThreadUpdate(
                thread,
                [.. thread.Messages.Where(message => message.Sequence > thread.RetrievedThrough)]))
            .Where(update => update.NewMessages.Count > 0)
            .ToList();

        return new MessageUpdatesResult.Updates(changed);
    }
}

/// <summary>One thread's high-water sequence to ack, as sent by the client.</summary>
public sealed record ThreadRetrievalAck(MessageThreadId ThreadId, int ThroughSequence);

/// <summary>One thread's watermark as it stands after the ack, echoed back so a client can see the
/// clamp take effect without a second call.</summary>
public sealed record ThreadWatermark(MessageThreadId ThreadId, int RetrievedThrough);

public union AckMessageUpdatesResult(AckMessageUpdatesResult.Acknowledged, AckMessageUpdatesResult.AccessDenied)
{
    /// <summary>
    /// <paramref name="UnknownThreadIds"/> covers a thread id this phone does not own, the same way
    /// <see cref="ThreadQuery"/> reports a foreign thread as not-found rather than forbidden: whether
    /// some other number holds a given thread id is not this caller's business.
    /// </summary>
    public record Acknowledged(IReadOnlyList<ThreadWatermark> Watermarks, IReadOnlyList<MessageThreadId> UnknownThreadIds);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// Advances the retrieval watermark on each named thread. A half-processed batch is safe to send as
/// is — a thread is simply left off <paramref name="Acks"/> until the caller has actually finished
/// with it — and re-sending the same ack twice is a no-op, because
/// <see cref="MessageThread.MarkRetrievedThrough"/> applies as a max.
/// </summary>
public sealed record AckMessageUpdatesCommand(PhoneDeviceId PhoneId, IReadOnlyList<ThreadRetrievalAck> Acks)
    : IRequest<AckMessageUpdatesResult>;

public sealed class AckMessageUpdatesHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<AckMessageUpdatesCommand, AckMessageUpdatesResult>
{
    public async ValueTask<AckMessageUpdatesResult> Handle(AckMessageUpdatesCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new AckMessageUpdatesResult.AccessDenied(access);
        }

        var watermarks = new List<ThreadWatermark>();
        var unknown = new List<MessageThreadId>();

        // Take the highest per thread id first: a caller sending the same thread twice in one batch
        // (a client bug, or two merged polls) must not have the lower entry silently win by running
        // last.
        var highestPerThread = request.Acks
            .GroupBy(ack => ack.ThreadId)
            .Select(group => group.OrderByDescending(ack => ack.ThroughSequence).First());

        foreach (var ack in highestPerThread)
        {
            var thread = await threadRepository.FindByIdAsync(ack.ThreadId, cancellationToken);
            if (thread is null || thread.OwnerPhoneId != request.PhoneId)
            {
                unknown.Add(ack.ThreadId);
                continue;
            }

            // Load-bearing, not an optimisation: without this, a client polling on an idle timer and
            // acking unconditionally would append one ThreadRetrievedThrough event per poll, forever,
            // even though MarkRetrievedThrough's own Apply is a safe no-op either way.
            if (ack.ThroughSequence > thread.RetrievedThrough)
            {
                threadRepository.Append(thread.Id, thread.MarkRetrievedThrough(ack.ThroughSequence));
            }

            watermarks.Add(new ThreadWatermark(thread.Id, thread.RetrievedThrough));
        }

        await threadRepository.SaveChangesAsync(cancellationToken);

        return new AckMessageUpdatesResult.Acknowledged(watermarks, unknown);
    }
}

public union MarkThreadReadResult(
    MarkThreadReadResult.MarkedRead,
    MarkThreadReadResult.NotFound,
    MarkThreadReadResult.AccessDenied)
{
    public record MarkedRead;

    public record NotFound;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record MarkThreadReadCommand(PhoneDeviceId PhoneId, MessageThreadId ThreadId)
    : IRequest<MarkThreadReadResult>;

public sealed class MarkThreadReadHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository,
    IPhoneNotificationRepository notificationRepository)
    : IRequestHandler<MarkThreadReadCommand, MarkThreadReadResult>
{
    public async ValueTask<MarkThreadReadResult> Handle(MarkThreadReadCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new MarkThreadReadResult.AccessDenied(access);
        }

        var thread = await threadRepository.FindByIdAsync(request.ThreadId, cancellationToken);
        if (thread is null || thread.OwnerPhoneId != request.PhoneId)
        {
            return new MarkThreadReadResult.NotFound();
        }

        threadRepository.Append(request.ThreadId, thread.MarkRead());

        // Reading a conversation clears its banners even though acking a poll deliberately does not
        // — see PhoneNotification's own doc comment. One commit for the append and the notification
        // delete together, on the shared IPhoneSession.
        notificationRepository.DeleteForGroup(request.PhoneId, AppKey.Messages, request.ThreadId.Value.ToString());

        await threadRepository.SaveChangesAsync(cancellationToken);

        return new MarkThreadReadResult.MarkedRead();
    }
}
