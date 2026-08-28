using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Devices;

public union SuspendPhoneResult(
    SuspendPhoneResult.Suspended,
    SuspendPhoneResult.AlreadySuspended,
    SuspendPhoneResult.PhoneNotFound,
    SuspendPhoneResult.PhoneDeactivated)
{
    public record Suspended;

    public record AlreadySuspended;

    public record PhoneNotFound;

    public record PhoneDeactivated;
}

/// <summary>
/// External control over a number: staff today under `phone:enforce`, an in-game Police/State
/// faction later without the domain changing at all.
///
/// Deliberately has no acting-character check and takes no PIN — the point of an enforcement action
/// is that the owner does not consent to it. A suspended phone can neither send nor receive, and
/// messages addressed to it are dropped rather than queued, since holding them for later would make
/// the lock a delay instead of a block. Nothing is lost: contacts, threads and the blocklist all
/// survive, and a restore brings the number back whole.
/// </summary>
public sealed record SuspendPhoneCommand(PhoneDeviceId PhoneId, string Reason) : IRequest<SuspendPhoneResult>;

public sealed class SuspendPhoneHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<SuspendPhoneCommand, SuspendPhoneResult>
{
    public async ValueTask<SuspendPhoneResult> Handle(SuspendPhoneCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new SuspendPhoneResult.PhoneNotFound();
        }

        if (phone.Status == PhoneStatus.Deactivated)
        {
            return new SuspendPhoneResult.PhoneDeactivated();
        }

        if (phone.Status == PhoneStatus.Suspended)
        {
            return new SuspendPhoneResult.AlreadySuspended();
        }

        phoneRepository.Append(request.PhoneId, phone.Suspend(request.Reason));
        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new SuspendPhoneResult.Suspended();
    }
}

public union RestorePhoneResult(
    RestorePhoneResult.Restored,
    RestorePhoneResult.NotSuspended,
    RestorePhoneResult.PhoneNotFound)
{
    public record Restored;

    /// <summary>Covers a deactivated phone too: deactivation is terminal, and a restore must not resurrect a retired number.</summary>
    public record NotSuspended;

    public record PhoneNotFound;
}

public sealed record RestorePhoneCommand(PhoneDeviceId PhoneId) : IRequest<RestorePhoneResult>;

public sealed class RestorePhoneHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<RestorePhoneCommand, RestorePhoneResult>
{
    public async ValueTask<RestorePhoneResult> Handle(RestorePhoneCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new RestorePhoneResult.PhoneNotFound();
        }

        if (phone.Status != PhoneStatus.Suspended)
        {
            return new RestorePhoneResult.NotSuspended();
        }

        phoneRepository.Append(request.PhoneId, phone.Restore());
        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new RestorePhoneResult.Restored();
    }
}
