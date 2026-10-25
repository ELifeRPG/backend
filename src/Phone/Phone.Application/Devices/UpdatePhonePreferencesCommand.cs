using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Devices;

public union UpdatePhonePreferencesResult(
    UpdatePhonePreferencesResult.Updated,
    UpdatePhonePreferencesResult.Invalid,
    UpdatePhonePreferencesResult.PhoneNotFound,
    UpdatePhonePreferencesResult.NotAuthorized,
    UpdatePhonePreferencesResult.PhoneDeactivated)
{
    public record Updated(PhonePreferences Preferences);

    public record Invalid(string Reason);

    public record PhoneNotFound;

    public record NotAuthorized;

    public record PhoneDeactivated;
}

/// <summary>
/// Null fields are left unchanged. Takes the owner, or the PIN from whoever else is holding the
/// phone — the same gate as the PIN change, and like it, deliberately does not require the phone to
/// be powered on: these are properties of the device, not something an app does.
/// </summary>
public sealed record UpdatePhonePreferencesCommand(
    PhoneDeviceId PhoneId,
    PhoneActor Actor,
    int? LayoutVersion,
    string? HomeLayout,
    string? Wallpaper,
    bool? DoNotDisturb) : IRequest<UpdatePhonePreferencesResult>;

public sealed class UpdatePhonePreferencesHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhonePreferencesRepository preferencesRepository)
    : IRequestHandler<UpdatePhonePreferencesCommand, UpdatePhonePreferencesResult>
{
    public async ValueTask<UpdatePhonePreferencesResult> Handle(UpdatePhonePreferencesCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new UpdatePhonePreferencesResult.PhoneNotFound();
        }

        if (!PhoneAccessPolicy.IsAuthorized(phone, request.Actor))
        {
            return new UpdatePhonePreferencesResult.NotAuthorized();
        }

        if (phone.Status == PhoneStatus.Deactivated)
        {
            return new UpdatePhonePreferencesResult.PhoneDeactivated();
        }

        var preferences = await preferencesRepository.FindAsync(request.PhoneId, cancellationToken)
            ?? PhonePreferences.Default(request.PhoneId);

        try
        {
            preferences.Update(request.LayoutVersion, request.HomeLayout, request.Wallpaper, request.DoNotDisturb);
        }
        catch (InvalidPhonePreferencesException exception)
        {
            return new UpdatePhonePreferencesResult.Invalid(exception.Message);
        }

        preferencesRepository.Store(preferences);
        await preferencesRepository.SaveChangesAsync(cancellationToken);

        return new UpdatePhonePreferencesResult.Updated(preferences);
    }
}
