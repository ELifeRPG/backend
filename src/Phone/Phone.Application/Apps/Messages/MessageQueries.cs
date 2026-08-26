using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public union ThreadsResult(ThreadsResult.Threads, ThreadsResult.AccessDenied)
{
    public record Threads(IReadOnlyList<MessageThread> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ThreadsQuery(SimCardId SimCardId, CharacterId ActingCharacterId) : IRequest<ThreadsResult>;

public sealed class ThreadsHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadsQuery, ThreadsResult>
{
    public async ValueTask<ThreadsResult> Handle(ThreadsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Messages,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        return access is PhoneAccessResult.Granted
            ? new ThreadsResult.Threads(await threadRepository.FindBySimAsync(request.SimCardId, cancellationToken))
            : new ThreadsResult.AccessDenied(access);
    }
}

public union ThreadResult(ThreadResult.Found, ThreadResult.NotFound, ThreadResult.AccessDenied)
{
    public record Found(MessageThread Thread);

    public record NotFound;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ThreadQuery(SimCardId SimCardId, CharacterId ActingCharacterId, MessageThreadId ThreadId)
    : IRequest<ThreadResult>;

public sealed class ThreadHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<ThreadQuery, ThreadResult>
{
    public async ValueTask<ThreadResult> Handle(ThreadQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Messages,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new ThreadResult.AccessDenied(access);
        }

        var thread = await threadRepository.FindByIdAsync(request.ThreadId, cancellationToken);

        // A thread belonging to a different SIM reads as absent rather than forbidden: whether some
        // other number holds a given thread id is not this caller's business.
        return thread is null || thread.OwnerSimCardId != request.SimCardId
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

public sealed record MarkThreadReadCommand(SimCardId SimCardId, CharacterId ActingCharacterId, MessageThreadId ThreadId)
    : IRequest<MarkThreadReadResult>;

public sealed class MarkThreadReadHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<MarkThreadReadCommand, MarkThreadReadResult>
{
    public async ValueTask<MarkThreadReadResult> Handle(MarkThreadReadCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Messages,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new MarkThreadReadResult.AccessDenied(access);
        }

        var thread = await threadRepository.FindByIdAsync(request.ThreadId, cancellationToken);
        if (thread is null || thread.OwnerSimCardId != request.SimCardId)
        {
            return new MarkThreadReadResult.NotFound();
        }

        threadRepository.Append(request.ThreadId, thread.MarkRead());
        await threadRepository.SaveChangesAsync(cancellationToken);

        return new MarkThreadReadResult.MarkedRead();
    }
}
