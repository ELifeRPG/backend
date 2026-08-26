using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// A handset: a host that supplies power, a capability tier and installed apps. It deliberately owns
/// no contacts, no threads and no blocklist — those live on the <see cref="SimCard"/>, which is why
/// moving a SIM carries everything with it and leaves the handset holding nothing.
///
/// <see cref="BoundCharacterId"/> is the biolock. It is recorded here but not enforced here: like
/// <c>BankAccount.Withdraw</c>'s <c>isAuthorized</c>, the check belongs to the caller — see
/// <c>PhoneAccessPolicy</c> — because deciding it needs state this aggregate may not load.
/// </summary>
public class PhoneDevice
{
    [JsonInclude]
    public PhoneDeviceId Id { get; private set; }

    [JsonInclude]
    public PhoneModelId ModelId { get; private set; }

    [JsonInclude]
    public CharacterId BoundCharacterId { get; private set; }

    [JsonInclude]
    public bool IsPoweredOn { get; private set; }

    [JsonInclude]
    public List<SimCardId> InstalledSims { get; private set; } = [];

    [JsonInclude]
    public List<InstalledApp> InstalledApps { get; private set; } = [];

    public static PhoneDevice Create(PhoneDeviceProvisioned domainEvent)
    {
        var device = new PhoneDevice();
        device.Apply(domainEvent);
        return device;
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
    /// The model is passed in rather than looked up: Domain may not reach for a repository, and both
    /// types live in this module so there is no reason to smuggle the slot count through a primitive.
    /// </summary>
    public SimInstalledIntoDevice InstallSim(SimCardId simCardId, PhoneModel model)
    {
        if (InstalledSims.Contains(simCardId))
        {
            throw new SimAlreadyInDeviceException($"SIM card {simCardId} is already in device {Id}.");
        }

        if (InstalledSims.Count >= model.SimSlots)
        {
            throw new SimSlotsFullException($"Device {Id} has no free SIM slot ({model.SimSlots} in total).");
        }

        var domainEvent = new SimInstalledIntoDevice(Id, simCardId);
        Apply(domainEvent);
        return domainEvent;
    }

    public SimEjectedFromDevice EjectSim(SimCardId simCardId)
    {
        if (!InstalledSims.Contains(simCardId))
        {
            throw new SimNotInDeviceException($"SIM card {simCardId} is not in device {Id}.");
        }

        var domainEvent = new SimEjectedFromDevice(Id, simCardId);
        Apply(domainEvent);
        return domainEvent;
    }

    public AppInstalled InstallApp(AppKey key, PhoneModel model)
    {
        // Where tier actually bites: a burner refuses the apps a smartphone advertises.
        if (!model.Supports(key))
        {
            throw new AppNotSupportedException($"Model {model.DisplayName} does not support app '{key}'.");
        }

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

    public void Apply(PhoneDeviceProvisioned domainEvent)
    {
        Id = domainEvent.Id;
        ModelId = domainEvent.ModelId;
        BoundCharacterId = domainEvent.BoundCharacterId;

        // Provisioned powered off on purpose: a device that woke up already receiving would deliver
        // messages before its owner ever touched it.
        IsPoweredOn = false;
    }

    public void Apply(PhoneDevicePoweredOn domainEvent) => IsPoweredOn = true;

    public void Apply(PhoneDevicePoweredOff domainEvent) => IsPoweredOn = false;

    public void Apply(SimInstalledIntoDevice domainEvent) => InstalledSims.Add(domainEvent.SimCardId);

    public void Apply(SimEjectedFromDevice domainEvent) => InstalledSims.Remove(domainEvent.SimCardId);

    public void Apply(AppInstalled domainEvent) => InstalledApps.Add(new InstalledApp(domainEvent.Key));

    public void Apply(AppUninstalled domainEvent) => InstalledApps.RemoveAll(app => app.Key == domainEvent.Key);
}
