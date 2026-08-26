using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices.Events;

namespace ELifeRPG.Phone.Application.Devices;

public union ProvisionPhoneDeviceResult(
    ProvisionPhoneDeviceResult.Provisioned,
    ProvisionPhoneDeviceResult.ModelNotFound)
{
    public record Provisioned(PhoneDeviceId DeviceId);

    public record ModelNotFound;
}

/// <summary>
/// Creates a handset bound to <paramref name="BoundCharacterId"/> — the biolock, set once and never
/// changed, which is what makes a looted phone a brick.
///
/// TODO(inventory): this is the only way a device comes into existence today, because eliferpg-core
/// has no per-character inventory to hand one over through. Shops can sell a phone Item, but nothing
/// connects that purchase to a device record, so provisioning is driven by the bridge under
/// `phone:provision` instead.
///
/// Closing that gap needs Reforger inventory persistence for *composed* items: a phone is a compound
/// object — an item instance carrying properties that reference the PhoneDeviceId it is, and the
/// SimCardId currently seated in it. Once an inventory item can hold that, provisioning moves into
/// the purchase flow (see PurchaseListingHandler's ICrossModuleTransaction) and buying, dropping and
/// looting a handset all become inventory operations rather than separate API calls.
/// </summary>
public sealed record ProvisionPhoneDeviceCommand(CharacterId BoundCharacterId, PhoneModelId ModelId)
    : IRequest<ProvisionPhoneDeviceResult>;

public sealed class ProvisionPhoneDeviceHandler(
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository)
    : IRequestHandler<ProvisionPhoneDeviceCommand, ProvisionPhoneDeviceResult>
{
    public async ValueTask<ProvisionPhoneDeviceResult> Handle(ProvisionPhoneDeviceCommand request, CancellationToken cancellationToken)
    {
        var model = await modelRepository.FindByIdAsync(request.ModelId, cancellationToken);
        if (model is null)
        {
            return new ProvisionPhoneDeviceResult.ModelNotFound();
        }

        var deviceId = new PhoneDeviceId(Guid.NewGuid());
        var provisioned = new PhoneDeviceProvisioned(deviceId, request.ModelId, request.BoundCharacterId);
        var device = PhoneDevice.Create(provisioned);

        deviceRepository.StartStream(device, provisioned);

        // A handset ships with the apps its model supports, the way a real one arrives with its
        // stock software. Uninstalling is then a deliberate act rather than a required setup step.
        foreach (var appKey in model.SupportedApps)
        {
            deviceRepository.Append(deviceId, device.InstallApp(appKey, model));
        }

        await deviceRepository.SaveChangesAsync(cancellationToken);

        return new ProvisionPhoneDeviceResult.Provisioned(deviceId);
    }
}
