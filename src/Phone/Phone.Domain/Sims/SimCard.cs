using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims.Events;

namespace ELifeRPG.Phone.Domain.Sims;

/// <summary>
/// The centre of gravity of the Phone module. A SIM owns the identity and the data: the number, the
/// blocklist, and — through <c>ContactBook</c> and <c>MessageThread</c>, both keyed by
/// <see cref="SimCardId"/> — the contacts and the message history. A device is only a host that
/// supplies power, a capability tier and installed apps.
///
/// Moving a SIM to another handset therefore carries everything with it, and leaves the old handset
/// holding nothing.
/// </summary>
public class SimCard
{
    [JsonInclude]
    public SimCardId Id { get; private set; }

    [JsonInclude]
    public PhoneNumber Number { get; private set; }

    /// <summary>
    /// Canonical string duplicate of <see cref="Number"/>, kept because Marten needs a plain string
    /// member to hang a unique index on and to translate an equality predicate against — a custom
    /// struct is neither. Written once by <see cref="Apply(SimCardIssued)"/>; a number never changes
    /// after issue, so the two can not drift.
    /// </summary>
    [JsonInclude]
    public string NumberValue { get; private set; } = string.Empty;

    [JsonInclude]
    public CharacterId RegisteredTo { get; private set; }

    [JsonInclude]
    public PhoneDeviceId? InstalledIn { get; private set; }

    [JsonInclude]
    public SimCardStatus Status { get; private set; }

    [JsonInclude]
    public List<PhoneNumber> BlockedNumbers { get; private set; } = [];

    public static SimCard Create(SimCardIssued domainEvent)
    {
        var sim = new SimCard();
        sim.Apply(domainEvent);
        return sim;
    }

    /// <summary>
    /// Suspended cards may still be installed and ejected — suspension is a network-side lock, not a
    /// physical one, and the card still fits the slot. What it may not do is send or receive, which
    /// is PhoneAccessPolicy's job. Deactivation is terminal and blocks even this.
    /// </summary>
    public SimCardInstalled InstallInto(PhoneDeviceId deviceId)
    {
        EnsureNotDeactivated();

        if (InstalledIn is not null)
        {
            throw new SimCardAlreadyInstalledException($"SIM card {Id} is already installed in device {InstalledIn}.");
        }

        var domainEvent = new SimCardInstalled(Id, deviceId);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardEjected Eject()
    {
        if (InstalledIn is not { } deviceId)
        {
            throw new SimCardNotInstalledException($"SIM card {Id} is not installed in any device.");
        }

        var domainEvent = new SimCardEjected(Id, deviceId);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardSuspended Suspend(string reason)
    {
        EnsureNotDeactivated();

        if (Status == SimCardStatus.Suspended)
        {
            throw new SimCardAlreadySuspendedException($"SIM card {Id} is already suspended.");
        }

        var domainEvent = new SimCardSuspended(Id, reason);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardRestored Restore()
    {
        // Guards on Suspended specifically rather than "not Active", so a restore can never
        // resurrect a deactivated number.
        if (Status != SimCardStatus.Suspended)
        {
            throw new SimCardNotSuspendedException($"SIM card {Id} is not suspended.");
        }

        var domainEvent = new SimCardRestored(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardDeactivated Deactivate()
    {
        EnsureNotDeactivated();

        var domainEvent = new SimCardDeactivated(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardNumberBlocked Block(PhoneNumber number)
    {
        EnsureNotDeactivated();

        if (number == Number)
        {
            throw new InvalidOperationException("A SIM card can not block its own number.");
        }

        if (IsBlocked(number))
        {
            throw new NumberAlreadyBlockedException($"{number} is already blocked on SIM card {Id}.");
        }

        var domainEvent = new SimCardNumberBlocked(Id, number);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimCardNumberUnblocked Unblock(PhoneNumber number)
    {
        if (!IsBlocked(number))
        {
            throw new NumberNotBlockedException($"{number} is not blocked on SIM card {Id}.");
        }

        var domainEvent = new SimCardNumberUnblocked(Id, number);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Compares canonical numbers, so a block placed on "5500-9911" also catches "+55009911".
    /// </summary>
    public bool IsBlocked(PhoneNumber number) => BlockedNumbers.Contains(number);

    public void Apply(SimCardIssued domainEvent)
    {
        Id = domainEvent.Id;
        Number = domainEvent.Number;
        NumberValue = domainEvent.Number.Value;
        RegisteredTo = domainEvent.RegisteredTo;
        Status = SimCardStatus.Active;
    }

    public void Apply(SimCardInstalled domainEvent) => InstalledIn = domainEvent.DeviceId;

    public void Apply(SimCardEjected domainEvent) => InstalledIn = null;

    public void Apply(SimCardSuspended domainEvent) => Status = SimCardStatus.Suspended;

    public void Apply(SimCardRestored domainEvent) => Status = SimCardStatus.Active;

    public void Apply(SimCardDeactivated domainEvent) => Status = SimCardStatus.Deactivated;

    public void Apply(SimCardNumberBlocked domainEvent) => BlockedNumbers.Add(domainEvent.Number);

    public void Apply(SimCardNumberUnblocked domainEvent) => BlockedNumbers.Remove(domainEvent.Number);

    private void EnsureNotDeactivated()
    {
        if (Status == SimCardStatus.Deactivated)
        {
            throw new SimCardDeactivatedException($"SIM card {Id} has been deactivated.");
        }
    }
}
