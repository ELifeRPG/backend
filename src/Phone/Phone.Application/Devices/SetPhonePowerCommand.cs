using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union SetPhonePowerResult(
    SetPhonePowerResult.PowerChanged,
    SetPhonePowerResult.AlreadyInState,
    SetPhonePowerResult.DeviceNotFound,
    SetPhonePowerResult.NotDeviceOwner)
{
    public record PowerChanged(bool IsPoweredOn);

    public record AlreadyInState(bool IsPoweredOn);

    public record DeviceNotFound;

    public record NotDeviceOwner;
}

/// <summary>
/// Powering on is also the moment queued messages arrive — see FlushPendingDeliveriesHandler, which
/// the Messages app hangs off this command.
/// </summary>
public sealed record SetPhonePowerCommand(PhoneDeviceId DeviceId, CharacterId ActingCharacterId, bool IsPoweredOn)
    : IRequest<SetPhonePowerResult>;

public sealed class SetPhonePowerHandler(IPhoneDeviceRepository deviceRepository, IMediator mediator)
    : IRequestHandler<SetPhonePowerCommand, SetPhonePowerResult>
{
    public async ValueTask<SetPhonePowerResult> Handle(SetPhonePowerCommand request, CancellationToken cancellationToken)
    {
        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new SetPhonePowerResult.DeviceNotFound();
        }

        if (device.BoundCharacterId != request.ActingCharacterId)
        {
            return new SetPhonePowerResult.NotDeviceOwner();
        }

        // Pre-checked rather than caught: a bridge retrying after a dropped response is an ordinary
        // occurrence, not an error, so it gets a plain "already like that" instead of a fault.
        if (device.IsPoweredOn == request.IsPoweredOn)
        {
            return new SetPhonePowerResult.AlreadyInState(device.IsPoweredOn);
        }

        deviceRepository.Append(request.DeviceId, request.IsPoweredOn ? device.PowerOn() : device.PowerOff());
        await deviceRepository.SaveChangesAsync(cancellationToken);

        if (request.IsPoweredOn)
        {
            // Powering on is one of the two moments a number becomes reachable again, so whatever
            // queued while the handset was off arrives now. Flushing after the commit rather than
            // inside it keeps a delivery failure from undoing the power change.
            foreach (var simCardId in device.InstalledSims)
            {
                await mediator.Send(new FlushPendingDeliveriesCommand(simCardId), cancellationToken);
            }
        }

        return new SetPhonePowerResult.PowerChanged(request.IsPoweredOn);
    }
}
