using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Common;

/// <summary>
/// The one guard chain every app command runs before touching anything. Adding an app buys all of
/// it for the cost of a single call — which is the whole point of separating the platform from the
/// apps that sit on it.
///
/// The ownership checks read <paramref name="actingCharacterId"/> from the request rather than from
/// the JWT, following the module-wide rule visible in WithdrawRequestDto and
/// PurchaseListingRequestDto: eliferpg-core never authorizes gameplay mutations off JWT identity.
/// That is also what lets the NPC service drive a phone through exactly these endpoints.
///
/// Note that SIM ownership and device biolock are checked separately and both against the acting
/// character, so a SIM only works in a handset bound to the same character — neither a stolen SIM
/// nor a stolen handset is worth anything.
/// </summary>
public union PhoneAccessResult(
    PhoneAccessResult.Granted,
    PhoneAccessResult.SimNotFound,
    PhoneAccessResult.NotSimOwner,
    PhoneAccessResult.SimSuspended,
    PhoneAccessResult.SimDeactivated,
    PhoneAccessResult.SimNotInstalled,
    PhoneAccessResult.DeviceNotFound,
    PhoneAccessResult.NotDeviceOwner,
    PhoneAccessResult.DevicePoweredOff,
    PhoneAccessResult.ModelNotFound,
    PhoneAccessResult.AppNotInstalled)
{
    public record Granted(SimCard SimCard, PhoneDevice Device, PhoneModel Model);

    public record SimNotFound;

    public record NotSimOwner;

    public record SimSuspended;

    public record SimDeactivated;

    public record SimNotInstalled;

    public record DeviceNotFound;

    public record NotDeviceOwner;

    public record DevicePoweredOff;

    /// <summary>The handset references a model that no longer exists — a data fault, not a player-triggerable case.</summary>
    public record ModelNotFound;

    public record AppNotInstalled;
}

internal static class PhoneAccessPolicy
{
    public static async ValueTask<PhoneAccessResult> AuthorizeAsync(
        SimCardId simCardId,
        CharacterId actingCharacterId,
        AppKey appKey,
        ISimCardRepository simCardRepository,
        IPhoneDeviceRepository deviceRepository,
        IPhoneModelRepository modelRepository,
        CancellationToken cancellationToken)
    {
        // Keyed by SIM because every app shipped so far keeps its state there
        // (AppCatalog.RequiresSim). The first app that does not — a camera, say — gets a
        // device-keyed overload alongside this one, and RequiresSim picks between them.
        var sim = await simCardRepository.FindByIdAsync(simCardId, cancellationToken);
        if (sim is null)
        {
            return new PhoneAccessResult.SimNotFound();
        }

        if (sim.RegisteredTo != actingCharacterId)
        {
            return new PhoneAccessResult.NotSimOwner();
        }

        // Suspension is reported distinctly from deactivation so the caller can tell "locked, and it
        // may come back" from "retired for good".
        if (sim.Status == SimCardStatus.Suspended)
        {
            return new PhoneAccessResult.SimSuspended();
        }

        if (sim.Status == SimCardStatus.Deactivated)
        {
            return new PhoneAccessResult.SimDeactivated();
        }

        if (sim.InstalledIn is not { } deviceId)
        {
            return new PhoneAccessResult.SimNotInstalled();
        }

        var device = await deviceRepository.FindByIdAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return new PhoneAccessResult.DeviceNotFound();
        }

        if (device.BoundCharacterId != actingCharacterId)
        {
            return new PhoneAccessResult.NotDeviceOwner();
        }

        if (!device.IsPoweredOn)
        {
            return new PhoneAccessResult.DevicePoweredOff();
        }

        if (!device.HasApp(appKey))
        {
            return new PhoneAccessResult.AppNotInstalled();
        }

        var model = await modelRepository.FindByIdAsync(device.ModelId, cancellationToken);
        if (model is null)
        {
            return new PhoneAccessResult.ModelNotFound();
        }

        return new PhoneAccessResult.Granted(sim, device, model);
    }
}
