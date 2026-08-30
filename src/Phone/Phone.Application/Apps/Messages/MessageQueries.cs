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
/// One thread's metadata paired with just the messages the caller has not seen. The thread itself is
/// a Marten-projected aggregate with private setters, so "the same thread carrying fewer messages"
/// cannot be expressed as a copy of it — and should not be: a poll answers "what is new", which is a
/// different question from "what does this thread hold".
/// </summary>
public sealed record MessageThreadUpdate(MessageThread Thread, IReadOnlyList<Message> NewMessages);

public union MessageUpdatesResult(MessageUpdatesResult.Updates, MessageUpdatesResult.AccessDenied)
{
    /// <summary>
    /// <paramref name="PolledAt"/> is the cursor to send on the next call. It is stamped before the
    /// read, so a message committed in the same instant lands on the next poll's side of the cursor
    /// instead of falling through it. Delivery is therefore at-least-once and a caller dedupes on
    /// <see cref="MessageId"/> — the safe direction, since the alternative is silently losing one.
    /// </summary>
    public record Updates(IReadOnlyList<MessageThreadUpdate> Threads, DateTimeOffset PolledAt);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// The polling counterpart to PhoneHub, for clients that cannot hold a socket: ArmA Reforger has no
/// SignalR client, so the Bridge asks what changed rather than being told. Like the hub, this is a
/// delivery convenience and never the source of truth — retention trimming (see MessageThread.Append)
/// can evict a message before a slow poller sees it, and ThreadQuery remains the authority.
///
/// A null <paramref name="Since"/> means "everything", which is what a client sends on connect.
/// Polling never marks anything read; MarkThreadReadCommand stays the only thing that does.
/// </summary>
public sealed record MessageUpdatesQuery(PhoneDeviceId PhoneId, DateTimeOffset? Since)
    : IRequest<MessageUpdatesResult>;

public sealed class MessageUpdatesHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository,
    TimeProvider timeProvider)
    : IRequestHandler<MessageUpdatesQuery, MessageUpdatesResult>
{
    public async ValueTask<MessageUpdatesResult> Handle(MessageUpdatesQuery request, CancellationToken cancellationToken)
    {
        // Stamped before the read, deliberately: a message that commits between here and the query
        // must fall after the cursor we hand back, not be skipped by it.
        var polledAt = timeProvider.GetUtcNow();

        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new MessageUpdatesResult.AccessDenied(access);
        }

        var threads = await threadRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);

        if (request.Since is not { } since)
        {
            return new MessageUpdatesResult.Updates(
                [.. threads.Select(thread => new MessageThreadUpdate(thread, thread.Messages))],
                polledAt);
        }

        // Filtered in memory rather than in the query: FindByPhoneAsync already loads this phone's
        // whole set for ThreadsHandler, and a phone holds few enough threads for that to be the
        // simpler trade.
        var changed = threads
            .Where(thread => thread.LastMessageAt > since)
            .Select(thread => new MessageThreadUpdate(
                thread,
                [.. thread.Messages.Where(message => message.SentAt > since)]))
            .ToList();

        return new MessageUpdatesResult.Updates(changed, polledAt);
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
    IMessageThreadRepository threadRepository)
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
        await threadRepository.SaveChangesAsync(cancellationToken);

        return new MarkThreadReadResult.MarkedRead();
    }
}
