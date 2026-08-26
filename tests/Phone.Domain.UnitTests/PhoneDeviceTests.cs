using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneDeviceTests
{
    private static PhoneModel Model(int simSlots = 1, AppKey[]? supportedApps = null) =>
        PhoneModel.Create(PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()),
            "Burner",
            tier: 1,
            itemId: null,
            simSlots: simSlots,
            supportedApps: supportedApps ?? [AppKey.Messages, AppKey.Contacts],
            contactLimit: 50,
            threadMessageLimit: 30,
            maxGroupParticipants: 5));

    private static PhoneDevice Device(PhoneModel model, CharacterId? owner = null) =>
        PhoneDevice.Create(new PhoneDeviceProvisioned(
            new PhoneDeviceId(Guid.NewGuid()),
            model.Id,
            owner ?? new CharacterId(Guid.NewGuid())));

    private static SimCardId ASim() => new(Guid.NewGuid());

    [Fact]
    public void Create_ProvisionsABoundDeviceThatIsPoweredOff()
    {
        // Powered off on arrival: a device that woke up already receiving would deliver messages
        // before its owner ever touched it.
        var owner = new CharacterId(Guid.NewGuid());
        var model = Model();

        var device = Device(model, owner);

        Assert.Equal(model.Id, device.ModelId);
        Assert.Equal(owner, device.BoundCharacterId);
        Assert.False(device.IsPoweredOn);
        Assert.Empty(device.InstalledSims);
        Assert.Empty(device.InstalledApps);
    }

    [Fact]
    public void PowerOn_TurnsTheDeviceOn()
    {
        var device = Device(Model());

        device.PowerOn();

        Assert.True(device.IsPoweredOn);
    }

    [Fact]
    public void PowerOn_WhenAlreadyOn_ThrowsPowerState()
    {
        var device = Device(Model());
        device.PowerOn();

        Assert.Throws<PhoneDevicePowerStateException>(() => device.PowerOn());
    }

    [Fact]
    public void PowerOff_WhenAlreadyOff_ThrowsPowerState()
    {
        Assert.Throws<PhoneDevicePowerStateException>(() => Device(Model()).PowerOff());
    }

    [Fact]
    public void InstallSim_WithAFreeSlot_RecordsTheSim()
    {
        var model = Model(simSlots: 1);
        var device = Device(model);
        var sim = ASim();

        var domainEvent = device.InstallSim(sim, model);

        Assert.Contains(sim, device.InstalledSims);
        Assert.Equal(sim, domainEvent.SimCardId);
    }

    [Fact]
    public void InstallSim_BeyondTheModelSlotCount_ThrowsSimSlotsFull()
    {
        var model = Model(simSlots: 1);
        var device = Device(model);
        device.InstallSim(ASim(), model);

        Assert.Throws<SimSlotsFullException>(() => device.InstallSim(ASim(), model));
    }

    [Fact]
    public void InstallSim_OnATwoSlotModel_AcceptsBoth()
    {
        var model = Model(simSlots: 2);
        var device = Device(model);

        device.InstallSim(ASim(), model);
        device.InstallSim(ASim(), model);

        Assert.Equal(2, device.InstalledSims.Count);
    }

    [Fact]
    public void InstallSim_WithTheSameSimTwice_ThrowsSimAlreadyInDevice()
    {
        var model = Model(simSlots: 2);
        var device = Device(model);
        var sim = ASim();
        device.InstallSim(sim, model);

        Assert.Throws<SimAlreadyInDeviceException>(() => device.InstallSim(sim, model));
    }

    [Fact]
    public void EjectSim_RemovesIt()
    {
        var model = Model();
        var device = Device(model);
        var sim = ASim();
        device.InstallSim(sim, model);

        device.EjectSim(sim);

        Assert.Empty(device.InstalledSims);
    }

    [Fact]
    public void EjectSim_ThatIsNotPresent_ThrowsSimNotInDevice()
    {
        Assert.Throws<SimNotInDeviceException>(() => Device(Model()).EjectSim(ASim()));
    }

    [Fact]
    public void InstallApp_WhenSupported_RecordsIt()
    {
        var model = Model(supportedApps: [AppKey.Messages]);
        var device = Device(model);

        device.InstallApp(AppKey.Messages, model);

        Assert.True(device.HasApp(AppKey.Messages));
    }

    [Fact]
    public void InstallApp_WhenTheModelDoesNotSupportIt_ThrowsAppNotSupported()
    {
        // Tier is enforced here: a burner refuses the apps a smartphone advertises.
        var model = Model(supportedApps: [AppKey.Messages]);
        var device = Device(model);

        Assert.Throws<AppNotSupportedException>(() => device.InstallApp(AppKey.Contacts, model));
    }

    [Fact]
    public void InstallApp_Twice_ThrowsAppAlreadyInstalled()
    {
        var model = Model();
        var device = Device(model);
        device.InstallApp(AppKey.Messages, model);

        Assert.Throws<AppAlreadyInstalledException>(() => device.InstallApp(AppKey.Messages, model));
    }

    [Fact]
    public void UninstallApp_RemovesIt()
    {
        var model = Model();
        var device = Device(model);
        device.InstallApp(AppKey.Messages, model);

        device.UninstallApp(AppKey.Messages);

        Assert.False(device.HasApp(AppKey.Messages));
    }

    [Fact]
    public void UninstallApp_ThatIsNotInstalled_ThrowsAppNotInstalled()
    {
        Assert.Throws<AppNotInstalledException>(() => Device(Model()).UninstallApp(AppKey.Messages));
    }

    [Fact]
    public void HasApp_ForAnUninstalledApp_IsFalse()
    {
        Assert.False(Device(Model()).HasApp(AppKey.Messages));
    }

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var deviceId = new PhoneDeviceId(Guid.NewGuid());
        var sim = ASim();
        var device = new PhoneDevice();

        device.Apply(new PhoneDeviceProvisioned(deviceId, new PhoneModelId(Guid.NewGuid()), new CharacterId(Guid.NewGuid())));
        device.Apply(new PhoneDevicePoweredOn(deviceId));
        device.Apply(new SimInstalledIntoDevice(deviceId, sim));
        device.Apply(new AppInstalled(deviceId, AppKey.Messages));
        device.Apply(new AppUninstalled(deviceId, AppKey.Messages));

        Assert.True(device.IsPoweredOn);
        Assert.Contains(sim, device.InstalledSims);
        Assert.False(device.HasApp(AppKey.Messages));
    }
}
