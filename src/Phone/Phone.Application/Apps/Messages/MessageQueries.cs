using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public union ThreadsResult(ThreadsResult.Threads, ThreadsResult.AccessDenied)
{
    public record Threads(IReadOnlyList<MessageThread> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ThreadsQuery(PhoneDeviceId PhoneId, PhoneActor Actor) : IRequest<ThreadsResult>;

public sealed class ThreadsHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadsQuery, ThreadsResult>
{
    public async ValueTask<ThreadsResult> Handle(ThreadsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Messages, phoneRepository, cancellationToken);

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

public sealed record ThreadQuery(PhoneDeviceId PhoneId, PhoneActor Actor, MessageThreadId ThreadId)
    : IRequest<ThreadResult>;

public sealed class ThreadHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadQuery, ThreadResult>
{
    public async ValueTask<ThreadResult> Handle(ThreadQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Messages, phoneRepository, cancellationToken);

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

public union MarkThreadReadResult(
    MarkThreadReadResult.MarkedRead,
    MarkThreadReadResult.NotFound,
    MarkThreadReadResult.AccessDenied)
{
    public record MarkedRead;

    public record NotFound;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record MarkThreadReadCommand(PhoneDeviceId PhoneId, PhoneActor Actor, MessageThreadId ThreadId)
    : IRequest<MarkThreadReadResult>;

public sealed class MarkThreadReadHandler(
    IPhoneDeviceRepository phoneRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<MarkThreadReadCommand, MarkThreadReadResult>
{
    public async ValueTask<MarkThreadReadResult> Handle(MarkThreadReadCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Messages, phoneRepository, cancellationToken);

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
