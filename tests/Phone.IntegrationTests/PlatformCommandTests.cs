using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Application.Sims;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the platform command layer: provisioning, power, SIM
/// seating, blocking and enforcement.
/// </summary>
public sealed class PlatformCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>
    /// A union result's `is` pattern only binds against its static type, so the match happens at the
    /// call site and this just reports it — same idiom as the Shops and Items integration tests.
    /// </summary>
    private static void ExpectCase(bool matched, string expected, object actual) =>
        Assert.True(matched, $"Expected {expected}, got {actual}");

    private async Task<T> Send<T>(IRequest<T> request)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, CancellationToken.None);
    }

    private async Task<PhoneModelId> CreateModel(int simSlots = 1, AppKey[]? apps = null, int contactLimit = 50)
    {
        var result = await Send(new CreatePhoneModelCommand(
            "Test handset", 1, null, simSlots, apps ?? [AppKey.Messages, AppKey.Contacts], contactLimit, 30, 5));
        return result is CreatePhoneModelResult.Created created
            ? created.ModelId
            : throw new XunitException($"Expected Created, got {result}");
    }

    private async Task<PhoneDeviceId> ProvisionDevice(PhoneModelId modelId, CharacterId owner)
    {
        var result = await Send(new ProvisionPhoneDeviceCommand(owner, modelId));
        return result is ProvisionPhoneDeviceResult.Provisioned provisioned
            ? provisioned.DeviceId
            : throw new XunitException($"Expected Provisioned, got {result}");
    }

    private async Task<(SimCardId Id, PhoneNumber Number)> ProvisionSim(CharacterId owner)
    {
        var result = await Send(new ProvisionSimCardCommand(owner));
        return result is ProvisionSimCardResult.Provisioned provisioned
            ? (provisioned.SimCardId, provisioned.Number)
            : throw new XunitException($"Expected Provisioned, got {result}");
    }

    [Fact]
    public async Task CreatePhoneModel_WithAnImpossibleDefinition_IsRejectedAsInvalid()
    {
        // Validation belongs to PhoneModel.Define; the handler's job is to turn it into a 400 rather
        // than letting an ArgumentException become a 500.
        var result = await Send(new CreatePhoneModelCommand("Broken", 1, null, 0, [AppKey.Messages], 50, 30, 5));

        var result1 = result;

        ExpectCase(result1 is CreatePhoneModelResult.InvalidDefinition, "InvalidDefinition", result1);
    }

    [Fact]
    public async Task ProvisionPhoneDevice_ShipsWithTheModelsAppsAndPoweredOff()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), owner);

        var lookup = await Send(new PhoneDeviceLookupQuery(deviceId));
        if (lookup is not PhoneDeviceLookupResult.Found found)
        {
            throw new XunitException($"Expected Found, got {lookup}");
        }

        var device = found.Device;

        Assert.False(device.IsPoweredOn);
        Assert.True(device.HasApp(AppKey.Messages));
        Assert.True(device.HasApp(AppKey.Contacts));
        Assert.Equal(owner, device.BoundCharacterId);
    }

    [Fact]
    public async Task ProvisionPhoneDevice_WithAnUnknownModel_IsRejected()
    {
        var result = await Send(new ProvisionPhoneDeviceCommand(new CharacterId(Guid.NewGuid()), new PhoneModelId(Guid.NewGuid())));

        var result2 = result;

        ExpectCase(result2 is ProvisionPhoneDeviceResult.ModelNotFound, "ModelNotFound", result2);
    }

    [Fact]
    public async Task SetPhonePower_TogglesThenReportsARepeatAsAlreadyInState()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), owner);

        var result3 = await Send(new SetPhonePowerCommand(deviceId, owner, true));

        ExpectCase(result3 is SetPhonePowerResult.PowerChanged, "PowerChanged", result3);

        // A bridge retrying after a dropped response is ordinary, not an error.
        var result4 = await Send(new SetPhonePowerCommand(deviceId, owner, true));
        ExpectCase(result4 is SetPhonePowerResult.AlreadyInState, "AlreadyInState", result4);
    }

    [Fact]
    public async Task SetPhonePower_ByAnotherCharacter_IsRefusedByTheBiolock()
    {
        var deviceId = await ProvisionDevice(await CreateModel(), new CharacterId(Guid.NewGuid()));

        var result = await Send(new SetPhonePowerCommand(deviceId, new CharacterId(Guid.NewGuid()), true));

        var result5 = result;

        ExpectCase(result5 is SetPhonePowerResult.NotDeviceOwner, "NotDeviceOwner", result5);
    }

    [Fact]
    public async Task ProvisionSimCard_IssuesDistinctNumbers()
    {
        var owner = new CharacterId(Guid.NewGuid());

        var first = await ProvisionSim(owner);
        var second = await ProvisionSim(owner);

        Assert.NotEqual(first.Number, second.Number);
        Assert.Equal(2, (await Send(new CharacterSimCardsQuery(owner))).Count);
    }

    [Fact]
    public async Task InstallSim_SeatsItInBothDirections()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), owner);
        var sim = await ProvisionSim(owner);

        var result6 = await Send(new InstallSimCommand(deviceId, sim.Id, owner));

        ExpectCase(result6 is InstallSimResult.Installed, "Installed", result6);

        var seated = await Send(new DeviceSimCardsQuery(deviceId));
        Assert.Equal(sim.Id, Assert.Single(seated).Id);
        Assert.Equal(deviceId, Assert.Single(seated).InstalledIn);
    }

    [Fact]
    public async Task InstallSim_WhenTheCharacterOwnsTheSimButNotTheDevice_IsRefused()
    {
        // The two ownership checks are what make a stolen handset worthless: holding someone's phone
        // does not let you put your own SIM in it.
        var simOwner = new CharacterId(Guid.NewGuid());
        var deviceOwner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), deviceOwner);
        var sim = await ProvisionSim(simOwner);

        var result = await Send(new InstallSimCommand(deviceId, sim.Id, simOwner));

        var result7 = result;

        ExpectCase(result7 is InstallSimResult.NotDeviceOwner, "NotDeviceOwner", result7);
    }

    [Fact]
    public async Task InstallSim_WhenTheCharacterOwnsTheDeviceButNotTheSim_IsRefused()
    {
        var deviceOwner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), deviceOwner);
        var sim = await ProvisionSim(new CharacterId(Guid.NewGuid()));

        var result = await Send(new InstallSimCommand(deviceId, sim.Id, deviceOwner));

        var result8 = result;

        ExpectCase(result8 is InstallSimResult.NotSimOwner, "NotSimOwner", result8);
    }

    [Fact]
    public async Task InstallSim_BeyondTheModelSlotCount_IsRefused()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(simSlots: 1), owner);
        var first = await ProvisionSim(owner);
        var second = await ProvisionSim(owner);
        await Send(new InstallSimCommand(deviceId, first.Id, owner));

        var result = await Send(new InstallSimCommand(deviceId, second.Id, owner));

        var result9 = result;

        ExpectCase(result9 is InstallSimResult.NoFreeSimSlot, "NoFreeSimSlot", result9);
    }

    [Fact]
    public async Task EjectSim_ThenInstallElsewhere_MovesTheSimBetweenHandsets()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var modelId = await CreateModel(simSlots: 1);
        var firstDevice = await ProvisionDevice(modelId, owner);
        var secondDevice = await ProvisionDevice(modelId, owner);
        var sim = await ProvisionSim(owner);
        await Send(new InstallSimCommand(firstDevice, sim.Id, owner));

        var result10 = await Send(new EjectSimCommand(firstDevice, sim.Id, owner));

        ExpectCase(result10 is EjectSimResult.Ejected, "Ejected", result10);
        var result11 = await Send(new InstallSimCommand(secondDevice, sim.Id, owner));
        ExpectCase(result11 is InstallSimResult.Installed, "Installed", result11);

        Assert.Empty(await Send(new DeviceSimCardsQuery(firstDevice)));
        Assert.Single(await Send(new DeviceSimCardsQuery(secondDevice)));
    }

    [Fact]
    public async Task InstallApp_ThatTheModelDoesNotSupport_IsRefused()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(apps: [AppKey.Messages]), owner);

        var result = await Send(new InstallAppCommand(deviceId, owner, AppKey.Contacts));

        var result12 = result;

        ExpectCase(result12 is InstallAppResult.NotSupportedByModel, "NotSupportedByModel", result12);
    }

    [Fact]
    public async Task UninstallApp_ThenReinstall_IsAllowed()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), owner);

        var result13 = await Send(new UninstallAppCommand(deviceId, owner, AppKey.Contacts));

        ExpectCase(result13 is UninstallAppResult.Uninstalled, "Uninstalled", result13);
        var result14 = await Send(new UninstallAppCommand(deviceId, owner, AppKey.Contacts));
        ExpectCase(result14 is UninstallAppResult.NotInstalled, "NotInstalled", result14);
        var result15 = await Send(new InstallAppCommand(deviceId, owner, AppKey.Contacts));
        ExpectCase(result15 is InstallAppResult.Installed, "Installed", result15);
    }

    [Fact]
    public async Task BlockNumber_ThenUnblock_RoundTrips()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var sim = await ProvisionSim(owner);
        var nuisance = (await ProvisionSim(new CharacterId(Guid.NewGuid()))).Number;

        var result16 = await Send(new BlockNumberCommand(sim.Id, owner, nuisance));

        ExpectCase(result16 is BlockNumberResult.Blocked, "Blocked", result16);
        var result17 = await Send(new BlockNumberCommand(sim.Id, owner, nuisance));
        ExpectCase(result17 is BlockNumberResult.AlreadyBlocked, "AlreadyBlocked", result17);
        var result18 = await Send(new UnblockNumberCommand(sim.Id, owner, nuisance));
        ExpectCase(result18 is UnblockNumberResult.Unblocked, "Unblocked", result18);
        var result19 = await Send(new UnblockNumberCommand(sim.Id, owner, nuisance));
        ExpectCase(result19 is UnblockNumberResult.NotBlocked, "NotBlocked", result19);
    }

    [Fact]
    public async Task BlockNumber_AgainstOwnNumber_IsRefused()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var sim = await ProvisionSim(owner);

        var result20 = await Send(new BlockNumberCommand(sim.Id, owner, sim.Number));

        ExpectCase(result20 is BlockNumberResult.CannotBlockOwnNumber, "CannotBlockOwnNumber", result20);
    }

    [Fact]
    public async Task SuspendSim_NeedsNoOwnerConsentAndSurvivesARestoreIntact()
    {
        // Enforcement acts against the owner by design, so there is no acting-character check — and
        // a restore has to return the number with its blocklist untouched.
        var owner = new CharacterId(Guid.NewGuid());
        var sim = await ProvisionSim(owner);
        var nuisance = (await ProvisionSim(new CharacterId(Guid.NewGuid()))).Number;
        await Send(new BlockNumberCommand(sim.Id, owner, nuisance));

        var result21 = await Send(new SuspendSimCommand(sim.Id, "Police order"));

        ExpectCase(result21 is SuspendSimResult.Suspended, "Suspended", result21);
        var result22 = await Send(new SuspendSimCommand(sim.Id, "Police order"));
        ExpectCase(result22 is SuspendSimResult.AlreadySuspended, "AlreadySuspended", result22);
        var result23 = await Send(new RestoreSimCommand(sim.Id));
        ExpectCase(result23 is RestoreSimResult.Restored, "Restored", result23);
        var result24 = await Send(new RestoreSimCommand(sim.Id));
        ExpectCase(result24 is RestoreSimResult.NotSuspended, "NotSuspended", result24);

        var lookup = await Send(new SimCardLookupQuery(sim.Id));
        if (lookup is not SimCardLookupResult.Found found)
        {
            throw new XunitException($"Expected Found, got {lookup}");
        }

        var restored = found.SimCard;
        Assert.Equal(SimCardStatus.Active, restored.Status);
        Assert.True(restored.IsBlocked(nuisance));
    }

    [Fact]
    public async Task SuspendedSim_CanStillBeSeatedInAHandset()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var deviceId = await ProvisionDevice(await CreateModel(), owner);
        var sim = await ProvisionSim(owner);
        await Send(new SuspendSimCommand(sim.Id, "Police order"));

        // The lock is on the network, not the slot — PhoneAccessPolicy is what refuses to use it.
        var result25 = await Send(new InstallSimCommand(deviceId, sim.Id, owner));
        ExpectCase(result25 is InstallSimResult.Installed, "Installed", result25);
    }
}
