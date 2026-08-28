using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union SetPhonePowerResult(
    SetPhonePowerResult.PowerChanged,
    SetPhonePowerResult.AlreadyInState,
    SetPhonePowerResult.PhoneNotFound,
    SetPhonePowerResult.NotAuthorized)
{
    public record PowerChanged(bool IsPoweredOn);

    public record AlreadyInState(bool IsPoweredOn);

    public record PhoneNotFound;

    public record NotAuthorized;
}

/// <summary>
/// Powering on is also the moment queued messages arrive — see FlushPendingDeliveriesHandler, which
/// the Messages app hangs off this command.
/// </summary>
public sealed record SetPhonePowerCommand(PhoneDeviceId PhoneId, PhoneActor Actor, bool IsPoweredOn)
    : IRequest<SetPhonePowerResult>;

public sealed class SetPhonePowerHandler(IPhoneDeviceRepository phoneRepository, IMediator mediator)
    : IRequestHandler<SetPhonePowerCommand, SetPhonePowerResult>
{
    public async ValueTask<SetPhonePowerResult> Handle(SetPhonePowerCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new SetPhonePowerResult.PhoneNotFound();
        }

        if (!PhoneAccessPolicy.IsAuthorized(phone, request.Actor))
        {
            return new SetPhonePowerResult.NotAuthorized();
        }

        // Pre-checked rather than caught: a bridge retrying after a dropped response is an ordinary
        // occurrence, not an error, so it gets a plain "already like that" instead of a fault.
        if (phone.IsPoweredOn == request.IsPoweredOn)
        {
            return new SetPhonePowerResult.AlreadyInState(phone.IsPoweredOn);
        }

        phoneRepository.Append(request.PhoneId, request.IsPoweredOn ? phone.PowerOn() : phone.PowerOff());
        await phoneRepository.SaveChangesAsync(cancellationToken);

        if (request.IsPoweredOn)
        {
            // Powering on is one of the two moments a number becomes reachable again — installing
            // Messages is the other — so whatever queued while the phone was off arrives now.
            // Flushing after the commit rather than inside it keeps a delivery failure from undoing
            // the power change.
            await mediator.Send(new FlushPendingDeliveriesCommand(request.PhoneId), cancellationToken);
        }

        return new SetPhonePowerResult.PowerChanged(request.IsPoweredOn);
    }
}
