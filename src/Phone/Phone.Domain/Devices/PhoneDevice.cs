using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// The whole of the Phone module's identity and state: the number, the PIN, the blocklist, and —
/// through <c>ContactBook</c> and <c>MessageThread</c>, both keyed by <see cref="PhoneDeviceId"/> —
/// the contacts and the message history, plus power and installed apps.
///
/// This used to be split across a <c>SimCard</c> that owned the identity and a handset that hosted
/// it. Nothing could exercise that split — devices only ever appear through <c>phone:provision</c>,
/// so nobody could move a card between two handsets they did not have — and it cost every app
/// command a two-aggregate guard chain. A phone is now one thing with one number, and a character
/// may hold several, each with its own.
///
/// <see cref="Pin"/> is what replaces the old biolock. The biolock made possession worthless: the
/// backend answered only to the bound character, so a dropped handset was scrap. The PIN makes
/// possession worth something instead — the owner's client supplies it implicitly, and anyone else
/// who knows it can use the phone. Enforcing that is <c>PhoneAccessPolicy</c>'s job, not this
/// aggregate's, for the same reason <c>BankAccount.Withdraw</c> takes an <c>isAuthorized</c> bool:
/// deciding it needs state this aggregate may not load.
///
/// The PIN is stored in the clear on purpose. It is a game prop, not a credential — the only caller
/// is the Bridge holding a client-credentials token, so there is no untrusted party on the other end
/// to brute-force it, and hashing four digits would not stop anyone who can already reach the
/// endpoint. It must never be returned by a read DTO, moderation reads included.
/// </summary>
public class PhoneDevice
{
    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;

    [JsonInclude]
    public PhoneDeviceId Id { get; private set; }

    [JsonInclude]
    public PhoneNumber Number { get; private set; }

    /// <summary>
    /// Canonical string duplicate of <see cref="Number"/>, kept because Marten needs a plain string
    /// member to hang a unique index on and to translate an equality predicate against — a custom
    /// struct is neither. Written once by <see cref="Apply(PhoneDeviceProvisioned)"/>; a number
    /// never changes after provisioning, so the two can not drift.
    /// </summary>
    [JsonInclude]
    public string NumberValue { get; private set; } = string.Empty;

    [JsonInclude]
    public string Pin { get; private set; } = string.Empty;

    [JsonInclude]
    public CharacterId RegisteredTo { get; private set; }

    [JsonInclude]
    public PhoneStatus Status { get; private set; }

    [JsonInclude]
    public bool IsPoweredOn { get; private set; }

    [JsonInclude]
    public List<PhoneNumber> BlockedNumbers { get; private set; } = [];

    [JsonInclude]
    public List<InstalledApp> InstalledApps { get; private set; } = [];

    public static PhoneDevice Create(PhoneDeviceProvisioned domainEvent)
    {
        var device = new PhoneDevice();
        device.Apply(domainEvent);
        return device;
    }

    /// <summary>
    /// Digits only, <see cref="MinPinLength"/> to <see cref="MaxPinLength"/> of them: it is typed on
    /// an in-game keypad. Validated here rather than at the edge so a PIN set through any route —
    /// provisioning or a later change — obeys the same rule.
    /// </summary>
    public static string EnsurePin(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < MinPinLength or > MaxPinLength || !pin.All(char.IsAsciiDigit))
        {
            throw new InvalidPhonePinException($"A PIN must be {MinPinLength} to {MaxPinLength} digits.");
        }

        return pin;
    }

    /// <summary>Compared in full, never by prefix — the caller either knows it or does not.</summary>
    public bool HasPin(string? candidate) => !string.IsNullOrEmpty(candidate) && candidate == Pin;

    public PhonePinChanged ChangePin(string pin)
    {
        EnsureNotDeactivated();

        var domainEvent = new PhonePinChanged(Id, EnsurePin(pin));
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneDevicePoweredOn PowerOn()
    {
        if (IsPoweredOn)
        {
            throw new PhoneDevicePowerStateException($"Device {Id} is already powered on.");
        }

        var domainEvent = new PhoneDevicePoweredOn(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneDevicePoweredOff PowerOff()
    {
        if (!IsPoweredOn)
        {
            throw new PhoneDevicePowerStateException($"Device {Id} is already powered off.");
        }

        var domainEvent = new PhoneDevicePoweredOff(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Every phone runs every app in the catalog — there are no models and no capability tiers, so
    /// installing one is a player's choice rather than a permission. What it still governs is
    /// delivery: uninstalling Messages queues incoming messages instead of dropping them.
    /// </summary>
    public AppInstalled InstallApp(AppKey key)
    {
        EnsureNotDeactivated();

        if (HasApp(key))
        {
            throw new AppAlreadyInstalledException($"App '{key}' is already installed on device {Id}.");
        }

        var domainEvent = new AppInstalled(Id, key);
        Apply(domainEvent);
        return domainEvent;
    }

    public AppUninstalled UninstallApp(AppKey key)
    {
        if (!HasApp(key))
        {
            throw new AppNotInstalledException($"App '{key}' is not installed on device {Id}.");
        }

        var domainEvent = new AppUninstalled(Id, key);
        Apply(domainEvent);
        return domainEvent;
    }

    public bool HasApp(AppKey key) => InstalledApps.Any(app => app.Key == key);

    public PhoneSuspended Suspend(string reason)
    {
        EnsureNotDeactivated();

        if (Status == PhoneStatus.Suspended)
        {
            throw new PhoneAlreadySuspendedException($"Phone {Id} is already suspended.");
        }

        var domainEvent = new PhoneSuspended(Id, reason);
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneRestored Restore()
    {
        // Guards on Suspended specifically rather than "not Active", so a restore can never
        // resurrect a deactivated number.
        if (Status != PhoneStatus.Suspended)
        {
            throw new PhoneNotSuspendedException($"Phone {Id} is not suspended.");
        }

        var domainEvent = new PhoneRestored(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneDeactivated Deactivate()
    {
        EnsureNotDeactivated();

        var domainEvent = new PhoneDeactivated(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneNumberBlocked Block(PhoneNumber number)
    {
        EnsureNotDeactivated();

        if (number == Number)
        {
            throw new InvalidOperationException("A phone can not block its own number.");
        }

        if (IsBlocked(number))
        {
            throw new NumberAlreadyBlockedException($"{number} is already blocked on phone {Id}.");
        }

        var domainEvent = new PhoneNumberBlocked(Id, number);
        Apply(domainEvent);
        return domainEvent;
    }

    public PhoneNumberUnblocked Unblock(PhoneNumber number)
    {
        if (!IsBlocked(number))
        {
            throw new NumberNotBlockedException($"{number} is not blocked on phone {Id}.");
        }

        var domainEvent = new PhoneNumberUnblocked(Id, number);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Compares canonical numbers, so a block placed on "5500-9911" also catches "+55009911".
    /// </summary>
    public bool IsBlocked(PhoneNumber number) => BlockedNumbers.Contains(number);

    public void Apply(PhoneDeviceProvisioned domainEvent)
    {
        Id = domainEvent.Id;
        Number = domainEvent.Number;
        NumberValue = domainEvent.Number.Value;
        Pin = domainEvent.Pin;
        RegisteredTo = domainEvent.RegisteredTo;
        Status = PhoneStatus.Active;

        // Provisioned powered off on purpose: a device that woke up already receiving would deliver
        // messages before its owner ever touched it.
        IsPoweredOn = false;
    }

    public void Apply(PhonePinChanged domainEvent) => Pin = domainEvent.Pin;

    public void Apply(PhoneDevicePoweredOn domainEvent) => IsPoweredOn = true;

    public void Apply(PhoneDevicePoweredOff domainEvent) => IsPoweredOn = false;

    public void Apply(PhoneSuspended domainEvent) => Status = PhoneStatus.Suspended;

    public void Apply(PhoneRestored domainEvent) => Status = PhoneStatus.Active;

    public void Apply(PhoneDeactivated domainEvent) => Status = PhoneStatus.Deactivated;

    public void Apply(PhoneNumberBlocked domainEvent) => BlockedNumbers.Add(domainEvent.Number);

    public void Apply(PhoneNumberUnblocked domainEvent) => BlockedNumbers.Remove(domainEvent.Number);

    public void Apply(AppInstalled domainEvent) => InstalledApps.Add(new InstalledApp(domainEvent.Key));

    public void Apply(AppUninstalled domainEvent) => InstalledApps.RemoveAll(app => app.Key == domainEvent.Key);

    private void EnsureNotDeactivated()
    {
        if (Status == PhoneStatus.Deactivated)
        {
            throw new PhoneDeactivatedException($"Phone {Id} has been deactivated.");
        }
    }
}
