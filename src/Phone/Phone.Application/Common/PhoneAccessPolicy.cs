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
/// Deliberately actor-less. Possession is proven once, at power-on: SetPhonePowerCommand runs
/// <see cref="PhoneAccessPolicy.IsAuthorized"/> (owner, or the PIN from anyone else holding the
/// handset), and every app operation then requires IsPoweredOn below. Re-checking the actor here
/// would only re-prove what the power state already carries — and the acting character was never
/// recorded on an app operation anyway: OutboundMessageRecorded carries a PhoneNumber, not a
/// CharacterId, so it fed one boolean and was discarded.
///
/// The module-wide rule that ownership is request-borne rather than read off the JWT (see
/// WithdrawRequestDto and PurchaseListingRequestDto) is unchanged — it is what still lets the NPC
/// service drive a phone through these endpoints. It now applies at power-on rather than per call.
///
/// Known trade-off: IsPoweredOn is durable, not session-scoped, so it means "someone unlocked this
/// at some point", not "just now". The in-game lock screen is the real gate; this chain guards the
/// device's own state.
///
/// This used to be eight steps across a SIM and a handset, checking the acting character against
/// both so that neither a stolen card nor a stolen phone was worth anything. There is one aggregate
/// now, and the PIN replaced the biolock: possession plus the PIN is enough.
/// </summary>
public union PhoneAccessResult(
    PhoneAccessResult.Granted,
    PhoneAccessResult.PhoneNotFound,
    PhoneAccessResult.PhoneSuspended,
    PhoneAccessResult.PhoneDeactivated,
    PhoneAccessResult.PhonePoweredOff,
    PhoneAccessResult.AppNotInstalled)
{
    public record Granted(PhoneDevice Phone);

    public record PhoneNotFound;

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
        AppKey appKey,
        IPhoneDeviceRepository phoneRepository,
        CancellationToken cancellationToken)
    {
        var phone = await phoneRepository.FindByIdAsync(phoneId, cancellationToken);
        if (phone is null)
        {
            return new PhoneAccessResult.PhoneNotFound();
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
