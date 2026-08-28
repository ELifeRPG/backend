using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Devices;

public union ChangePinResult(
    ChangePinResult.Changed,
    ChangePinResult.InvalidPin,
    ChangePinResult.PhoneNotFound,
    ChangePinResult.NotAuthorized,
    ChangePinResult.PhoneDeactivated)
{
    public record Changed;

    public record InvalidPin(string Reason);

    public record PhoneNotFound;

    public record NotAuthorized;

    public record PhoneDeactivated;
}

/// <summary>
/// Changing the PIN takes the owner, or the current PIN from whoever else is holding the phone —
/// the same gate as every other action on it, and for the same reason: a handset someone is holding
/// with the PIN is theirs to use, and locking the previous owner out is part of that.
///
/// Deliberately does not require the phone to be powered on. Setting a PIN is a property of the
/// device, not something an app does, so it stays reachable the way power itself does.
/// </summary>
public sealed record ChangePinCommand(PhoneDeviceId PhoneId, PhoneActor Actor, string NewPin)
    : IRequest<ChangePinResult>;

public sealed class ChangePinHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<ChangePinCommand, ChangePinResult>
{
    public async ValueTask<ChangePinResult> Handle(ChangePinCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new ChangePinResult.PhoneNotFound();
        }

        if (!PhoneAccessPolicy.IsAuthorized(phone, request.Actor))
        {
            return new ChangePinResult.NotAuthorized();
        }

        if (phone.Status == PhoneStatus.Deactivated)
        {
            return new ChangePinResult.PhoneDeactivated();
        }

        try
        {
            phoneRepository.Append(request.PhoneId, phone.ChangePin(request.NewPin));
        }
        catch (InvalidPhonePinException exception)
        {
            return new ChangePinResult.InvalidPin(exception.Message);
        }

        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new ChangePinResult.Changed();
    }
}
