using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Common;

/// <summary>
/// Who is acting on a phone, and what they offered to prove they may.
///
/// <paramref name="Pin"/> is null for the ordinary case — the owner's own handset, where the mod
/// fills the PIN in implicitly and the backend never needs to see it. It is supplied when someone
/// else is holding the phone.
/// </summary>
public sealed record PhoneActor(CharacterId CharacterId, string? Pin = null);

/// <summary>
/// The one guard chain every app command runs before touching anything. Adding an app buys all of
/// it for the cost of a single call — which is the whole point of separating the platform from the
/// apps that sit on it.
///
/// The ownership checks read the acting character from the request rather than from the JWT,
/// following the module-wide rule visible in WithdrawRequestDto and PurchaseListingRequestDto:
/// eliferpg-core never authorizes gameplay mutations off JWT identity. That is also what lets the
/// NPC service drive a phone through exactly these endpoints.
///
/// This used to be eight steps across a SIM and a handset, checking the acting character against
/// both so that neither a stolen card nor a stolen phone was worth anything. There is one aggregate
/// now, and the PIN replaced the biolock: possession plus the PIN is enough.
/// </summary>
public union PhoneAccessResult(
    PhoneAccessResult.Granted,
    PhoneAccessResult.PhoneNotFound,
    PhoneAccessResult.NotAuthorized,
    PhoneAccessResult.PhoneSuspended,
    PhoneAccessResult.PhoneDeactivated,
    PhoneAccessResult.PhonePoweredOff,
    PhoneAccessResult.AppNotInstalled)
{
    public record Granted(PhoneDevice Phone);

    public record PhoneNotFound;

    /// <summary>
    /// Neither the registered owner nor a correct PIN. Deliberately one case rather than two: a
    /// caller must not be able to tell "wrong PIN" from "not your phone", which would make the
    /// endpoint an oracle for whose phone this is.
    /// </summary>
    public record NotAuthorized;

    public record PhoneSuspended;

    public record PhoneDeactivated;

    public record PhonePoweredOff;

    public record AppNotInstalled;
}

internal static class PhoneAccessPolicy
{
    /// <summary>
    /// The owner acts freely; anyone else needs the PIN. Shared with the platform commands (power,
    /// apps, blocklist, PIN change), which want this check without the app and power steps.
    /// </summary>
    public static bool IsAuthorized(PhoneDevice phone, PhoneActor actor) =>
        phone.RegisteredTo == actor.CharacterId || phone.HasPin(actor.Pin);

    public static async ValueTask<PhoneAccessResult> AuthorizeAsync(
        PhoneDeviceId phoneId,
        PhoneActor actor,
        AppKey appKey,
        IPhoneDeviceRepository phoneRepository,
        CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(phoneId, cancellationToken);
        if (phone is null)
        {
            return new PhoneAccessResult.PhoneNotFound();
        }

        if (!IsAuthorized(phone, actor))
        {
            return new PhoneAccessResult.NotAuthorized();
        }

        // Suspension is reported distinctly from deactivation so the caller can tell "locked, and it
        // may come back" from "retired for good".
        if (phone.Status == PhoneStatus.Suspended)
        {
            return new PhoneAccessResult.PhoneSuspended();
        }

        if (phone.Status == PhoneStatus.Deactivated)
        {
            return new PhoneAccessResult.PhoneDeactivated();
        }

        if (!phone.IsPoweredOn)
        {
            return new PhoneAccessResult.PhonePoweredOff();
        }

        if (!phone.HasApp(appKey))
        {
            return new PhoneAccessResult.AppNotInstalled();
        }

        return new PhoneAccessResult.Granted(phone);
    }
}
