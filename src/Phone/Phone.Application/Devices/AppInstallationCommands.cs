using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Devices;

public union InstallAppResult(
    InstallAppResult.Installed,
    InstallAppResult.AlreadyInstalled,
    InstallAppResult.NotSupportedByModel,
    InstallAppResult.UnknownApp,
    InstallAppResult.DeviceNotFound,
    InstallAppResult.NotDeviceOwner,
    InstallAppResult.ModelNotFound)
{
    public record Installed;

    public record AlreadyInstalled;

    /// <summary>Where tier bites: a burner refuses what a smartphone advertises.</summary>
    public record NotSupportedByModel;

    public record UnknownApp;

    public record DeviceNotFound;

    public record NotDeviceOwner;

    public record ModelNotFound;
}

public sealed record InstallAppCommand(PhoneDeviceId DeviceId, CharacterId ActingCharacterId, AppKey AppKey)
    : IRequest<InstallAppResult>;

public sealed class InstallAppHandler(IPhoneDeviceRepository deviceRepository, IPhoneModelRepository modelRepository)
    : IRequestHandler<InstallAppCommand, InstallAppResult>
{
    public async ValueTask<InstallAppResult> Handle(InstallAppCommand request, CancellationToken cancellationToken)
    {
        if (!AppCatalog.Contains(request.AppKey))
        {
            return new InstallAppResult.UnknownApp();
        }

        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new InstallAppResult.DeviceNotFound();
        }

        if (device.BoundCharacterId != request.ActingCharacterId)
        {
            return new InstallAppResult.NotDeviceOwner();
        }

        var model = await modelRepository.FindByIdAsync(device.ModelId, cancellationToken);
        if (model is null)
        {
            return new InstallAppResult.ModelNotFound();
        }

        if (!model.Supports(request.AppKey))
        {
            return new InstallAppResult.NotSupportedByModel();
        }

        if (device.HasApp(request.AppKey))
        {
            return new InstallAppResult.AlreadyInstalled();
        }

        deviceRepository.Append(request.DeviceId, device.InstallApp(request.AppKey, model));
        await deviceRepository.SaveChangesAsync(cancellationToken);

        return new InstallAppResult.Installed();
    }
}

public union UninstallAppResult(
    UninstallAppResult.Uninstalled,
    UninstallAppResult.NotInstalled,
    UninstallAppResult.DeviceNotFound,
    UninstallAppResult.NotDeviceOwner)
{
    public record Uninstalled;

    public record NotInstalled;

    public record DeviceNotFound;

    public record NotDeviceOwner;
}

/// <summary>
/// Uninstalling loses nothing: an app's state lives on the SIM, not on the handset, so reinstalling
/// brings the contacts and threads straight back.
/// </summary>
public sealed record UninstallAppCommand(PhoneDeviceId DeviceId, CharacterId ActingCharacterId, AppKey AppKey)
    : IRequest<UninstallAppResult>;

public sealed class UninstallAppHandler(IPhoneDeviceRepository deviceRepository)
    : IRequestHandler<UninstallAppCommand, UninstallAppResult>
{
    public async ValueTask<UninstallAppResult> Handle(UninstallAppCommand request, CancellationToken cancellationToken)
    {
        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new UninstallAppResult.DeviceNotFound();
        }

        if (device.BoundCharacterId != request.ActingCharacterId)
        {
            return new UninstallAppResult.NotDeviceOwner();
        }

        if (!device.HasApp(request.AppKey))
        {
            return new UninstallAppResult.NotInstalled();
        }

        deviceRepository.Append(request.DeviceId, device.UninstallApp(request.AppKey));
        await deviceRepository.SaveChangesAsync(cancellationToken);

        return new UninstallAppResult.Uninstalled();
    }
}
