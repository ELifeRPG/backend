using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public union BlockNumberResult(
    BlockNumberResult.Blocked,
    BlockNumberResult.AlreadyBlocked,
    BlockNumberResult.CannotBlockOwnNumber,
    BlockNumberResult.AccessDenied)
{
    public record Blocked;

    public record AlreadyBlocked;

    public record CannotBlockOwnNumber;

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// The blocklist is the Messages app's, and blocking runs the same guard chain as sending: the
/// phone must be powered on with Messages installed.
///
/// It was a platform command until the routes moved under <c>/apps/{appKey}/</c>, reachable on a
/// powered-off phone on the reasoning that a later Calls app would honour the same list. That trade
/// is now the other way round — the URL says which app owns this, so the authorization has to agree
/// with it, and an app whose data you can edit while its host is switched off is the odder of the
/// two. A Calls app that wants to refuse the same numbers reads
/// <see cref="ELifeRPG.Phone.Domain.Devices.PhoneDevice.IsBlocked"/> like the send path does: the
/// list still lives on the aggregate, so only the guard moved, not the storage.
/// </summary>
public sealed record BlockNumberCommand(PhoneDeviceId PhoneId, PhoneActor Actor, PhoneNumber Number)
    : IRequest<BlockNumberResult>;

public sealed class BlockNumberHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<BlockNumberCommand, BlockNumberResult>
{
    public async ValueTask<BlockNumberResult> Handle(BlockNumberCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new BlockNumberResult.AccessDenied(access);
        }

        var phone = granted.Phone;

        if (request.Number == phone.Number)
        {
            return new BlockNumberResult.CannotBlockOwnNumber();
        }

        if (phone.IsBlocked(request.Number))
        {
            return new BlockNumberResult.AlreadyBlocked();
        }

        phoneRepository.Append(request.PhoneId, phone.Block(request.Number));
        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new BlockNumberResult.Blocked();
    }
}

public union UnblockNumberResult(
    UnblockNumberResult.Unblocked,
    UnblockNumberResult.NotBlocked,
    UnblockNumberResult.AccessDenied)
{
    public record Unblocked;

    public record NotBlocked;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record UnblockNumberCommand(PhoneDeviceId PhoneId, PhoneActor Actor, PhoneNumber Number)
    : IRequest<UnblockNumberResult>;

public sealed class UnblockNumberHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<UnblockNumberCommand, UnblockNumberResult>
{
    public async ValueTask<UnblockNumberResult> Handle(UnblockNumberCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Messages, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new UnblockNumberResult.AccessDenied(access);
        }

        var phone = granted.Phone;

        if (!phone.IsBlocked(request.Number))
        {
            return new UnblockNumberResult.NotBlocked();
        }

        phoneRepository.Append(request.PhoneId, phone.Unblock(request.Number));
        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new UnblockNumberResult.Unblocked();
    }
}
