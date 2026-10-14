using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union InstallAppResult(
    InstallAppResult.Installed,
    InstallAppResult.AlreadyInstalled,
    InstallAppResult.UnknownApp,
    InstallAppResult.PhoneNotFound,
    InstallAppResult.NotAuthorized,
    InstallAppResult.PhoneDeactivated)
{
    public record Installed;

    public record AlreadyInstalled;

    public record UnknownApp;

    public record PhoneNotFound;

    public record NotAuthorized;

    public record PhoneDeactivated;
}

/// <summary>
/// Every phone can run every app in the catalog — there are no models and no capability tiers, so
/// installing one is a player's choice rather than a permission the handset grants.
/// </summary>
public sealed record InstallAppCommand(PhoneDeviceId PhoneId, PhoneActor Actor, AppKey AppKey)
    : IRequest<InstallAppResult>;

public sealed class InstallAppHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<InstallAppCommand, InstallAppResult>
{
    public async ValueTask<InstallAppResult> Handle(InstallAppCommand request, CancellationToken cancellationToken)
    {
        if (!AppCatalog.Contains(request.AppKey))
        {
            return new InstallAppResult.UnknownApp();
        }

        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new InstallAppResult.PhoneNotFound();
        }

        if (!PhoneAccessPolicy.IsAuthorized(phone, request.Actor))
        {
            return new InstallAppResult.NotAuthorized();
        }

        if (phone.Status == PhoneStatus.Deactivated)
        {
            return new InstallAppResult.PhoneDeactivated();
        }

        if (phone.HasApp(request.AppKey))
        {
            return new InstallAppResult.AlreadyInstalled();
        }

        phoneRepository.Append(request.PhoneId, phone.InstallApp(request.AppKey));
        await phoneRepository.SaveChangesAsync(cancellationToken);

        // No pending-delivery flush here any more. Messages is a system app, so every phone already
        // has it and this path could only ever no-op; powering on remains the one moment a queued
        // message is handed over. See SetPhonePowerHandler.
        return new InstallAppResult.Installed();
    }
}

public union UninstallAppResult(
    UninstallAppResult.Uninstalled,
    UninstallAppResult.NotInstalled,
    UninstallAppResult.SystemApp,
    UninstallAppResult.PhoneNotFound,
    UninstallAppResult.NotAuthorized)
{
    public record Uninstalled;

    public record NotInstalled;

    /// <summary>The phone's own software, which no actor may remove — see <c>AppDefinition</c>.</summary>
    public record SystemApp;

    public record PhoneNotFound;

    public record NotAuthorized;
}

/// <summary>
/// Uninstalling loses nothing: contacts and threads are the phone's, not the app's, so reinstalling
/// brings them straight back.
///
/// Only a store app can be uninstalled at all. The domain enforces that too, but the check is
/// repeated here so the endpoint gets a case to switch on rather than an exception to catch — the
/// module-wide shape (ARCHITECTURE.md §9e).
/// </summary>
public sealed record UninstallAppCommand(PhoneDeviceId PhoneId, PhoneActor Actor, AppKey AppKey)
    : IRequest<UninstallAppResult>;

public sealed class UninstallAppHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<UninstallAppCommand, UninstallAppResult>
{
    public async ValueTask<UninstallAppResult> Handle(UninstallAppCommand request, CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken);
        if (phone is null)
        {
            return new UninstallAppResult.PhoneNotFound();
        }

        if (!PhoneAccessPolicy.IsAuthorized(phone, request.Actor))
        {
            return new UninstallAppResult.NotAuthorized();
        }

        if (AppCatalog.IsSystemApp(request.AppKey))
        {
            return new UninstallAppResult.SystemApp();
        }

        if (!phone.HasApp(request.AppKey))
        {
            return new UninstallAppResult.NotInstalled();
        }

        phoneRepository.Append(request.PhoneId, phone.UninstallApp(request.AppKey));
        await phoneRepository.SaveChangesAsync(cancellationToken);

        return new UninstallAppResult.Uninstalled();
    }
}
