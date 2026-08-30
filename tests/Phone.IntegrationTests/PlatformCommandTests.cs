using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the platform command layer: provisioning, the PIN, power,
/// apps and enforcement. Blocking lives with the Messages app now, so its tests do too.
/// </summary>
public sealed class PlatformCommandTests : IAsyncLifetime
{
    private const string Pin = "1234";

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

    private async Task<(PhoneDeviceId Id, PhoneNumber Number)> ProvisionPhone(CharacterId owner, string pin = Pin)
    {
        var result = await Send(new ProvisionPhoneCommand(owner, pin));
        return result is ProvisionPhoneResult.Provisioned provisioned
            ? (provisioned.PhoneId, provisioned.Number)
            : throw new XunitException($"Expected Provisioned, got {result}");
    }

    private async Task<PhoneDevice> Load(PhoneDeviceId phoneId)
    {
        var lookup = await Send(new PhoneDeviceLookupQuery(phoneId));
        return lookup is PhoneDeviceLookupResult.Found found
            ? found.Phone
            : throw new XunitException($"Expected Found, got {lookup}");
    }

    [Fact]
    public async Task ProvisionPhone_ShipsWithEveryAppAndPoweredOff()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);

        var loaded = await Load(phone.Id);

        Assert.False(loaded.IsPoweredOn);
        Assert.Equal(PhoneStatus.Active, loaded.Status);
        Assert.Equal(owner, loaded.RegisteredTo);
        Assert.Equal(phone.Number, loaded.Number);
        foreach (var key in AppCatalog.Entries.Keys)
        {
            Assert.True(loaded.HasApp(key), $"AppKey.{key} should ship installed.");
        }
    }

    [Fact]
    public async Task ProvisionPhone_IssuesDistinctNumbersAndBothAreTheCharactersOwn()
    {
        // A character may hold several phones, each with its own number — the replacement for
        // holding several SIMs.
        var owner = new CharacterId(Guid.NewGuid());

        var first = await ProvisionPhone(owner);
        var second = await ProvisionPhone(owner);

        Assert.NotEqual(first.Number, second.Number);
        Assert.Equal(2, (await Send(new CharacterPhonesQuery(owner))).Count);
    }

    [Fact]
    public async Task ProvisionPhone_WithAnUnusablePin_IsRejected()
    {
        var result = await Send(new ProvisionPhoneCommand(new CharacterId(Guid.NewGuid()), "12"));

        ExpectCase(result is ProvisionPhoneResult.InvalidPin, "InvalidPin", result);
    }

    // --- The PIN, which is what replaced the biolock ----------------------------------------

    [Fact]
    public async Task SetPhonePower_ByAnotherCharacterWithTheCorrectPin_IsAllowed()
    {
        // The whole point of dropping the biolock: possession plus the PIN is enough, so a handset
        // that changes hands is worth something.
        var phone = await ProvisionPhone(new CharacterId(Guid.NewGuid()), pin: "4711");
        var stranger = new PhoneActor(new CharacterId(Guid.NewGuid()), "4711");

        var result = await Send(new SetPhonePowerCommand(phone.Id, stranger, true));

        ExpectCase(result is SetPhonePowerResult.PowerChanged, "PowerChanged", result);
    }

    [Fact]
    public async Task SetPhonePower_ByAnotherCharacterWithTheWrongPin_IsRefused()
    {
        var phone = await ProvisionPhone(new CharacterId(Guid.NewGuid()), pin: "4711");
        var stranger = new PhoneActor(new CharacterId(Guid.NewGuid()), "0000");

        var result = await Send(new SetPhonePowerCommand(phone.Id, stranger, true));

        ExpectCase(result is SetPhonePowerResult.NotAuthorized, "NotAuthorized", result);
    }

    [Fact]
    public async Task SetPhonePower_ByAnotherCharacterWithNoPinAtAll_IsRefused()
    {
        var phone = await ProvisionPhone(new CharacterId(Guid.NewGuid()));
        var stranger = new PhoneActor(new CharacterId(Guid.NewGuid()));

        var result = await Send(new SetPhonePowerCommand(phone.Id, stranger, true));

        ExpectCase(result is SetPhonePowerResult.NotAuthorized, "NotAuthorized", result);
    }

    [Fact]
    public async Task SetPhonePower_ByTheOwnerWithoutAPin_IsAllowed()
    {
        // The owner's client never sends one; ownership is the fast path past the PIN.
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);

        var result = await Send(new SetPhonePowerCommand(phone.Id, new PhoneActor(owner), true));

        ExpectCase(result is SetPhonePowerResult.PowerChanged, "PowerChanged", result);
    }

    [Fact]
    public async Task ChangePin_ByAHolderWithTheCurrentPin_LocksTheOldOneOut()
    {
        var phone = await ProvisionPhone(new CharacterId(Guid.NewGuid()), pin: "4711");
        var stranger = new CharacterId(Guid.NewGuid());

        var changed = await Send(new ChangePinCommand(phone.Id, new PhoneActor(stranger, "4711"), "9876"));
        ExpectCase(changed is ChangePinResult.Changed, "Changed", changed);

        var withNewPin = await Send(new SetPhonePowerCommand(phone.Id, new PhoneActor(stranger, "9876"), true));
        ExpectCase(withNewPin is SetPhonePowerResult.PowerChanged, "PowerChanged", withNewPin);

        var withOldPin = await Send(new SetPhonePowerCommand(phone.Id, new PhoneActor(stranger, "4711"), false));
        ExpectCase(withOldPin is SetPhonePowerResult.NotAuthorized, "NotAuthorized", withOldPin);
    }

    [Fact]
    public async Task ChangePin_ToAnUnusablePin_IsRejected()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);

        var result = await Send(new ChangePinCommand(phone.Id, new PhoneActor(owner), "abcd"));

        ExpectCase(result is ChangePinResult.InvalidPin, "InvalidPin", result);
    }

    // --- Power and apps ---------------------------------------------------------------------

    [Fact]
    public async Task SetPhonePower_TogglesThenReportsARepeatAsAlreadyInState()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);

        var first = await Send(new SetPhonePowerCommand(phone.Id, new PhoneActor(owner), true));
        ExpectCase(first is SetPhonePowerResult.PowerChanged, "PowerChanged", first);

        // A bridge retrying after a dropped response is ordinary, not an error.
        var repeat = await Send(new SetPhonePowerCommand(phone.Id, new PhoneActor(owner), true));
        ExpectCase(repeat is SetPhonePowerResult.AlreadyInState, "AlreadyInState", repeat);
    }

    [Fact]
    public async Task UninstallApp_ThenReinstall_IsAllowed()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);
        var actor = new PhoneActor(owner);

        var uninstalled = await Send(new UninstallAppCommand(phone.Id, actor, AppKey.Contacts));
        ExpectCase(uninstalled is UninstallAppResult.Uninstalled, "Uninstalled", uninstalled);

        var repeat = await Send(new UninstallAppCommand(phone.Id, actor, AppKey.Contacts));
        ExpectCase(repeat is UninstallAppResult.NotInstalled, "NotInstalled", repeat);

        var reinstalled = await Send(new InstallAppCommand(phone.Id, actor, AppKey.Contacts));
        ExpectCase(reinstalled is InstallAppResult.Installed, "Installed", reinstalled);
    }

    // --- Enforcement ------------------------------------------------------------------------

    [Fact]
    public async Task SuspendPhone_NeedsNoOwnerConsentAndSurvivesARestoreIntact()
    {
        // Enforcement acts against the owner by design, so there is no acting character and no PIN —
        // and a restore has to return the number with its blocklist untouched.
        var owner = new CharacterId(Guid.NewGuid());
        var actor = new PhoneActor(owner);
        var phone = await ProvisionPhone(owner);
        var nuisance = (await ProvisionPhone(new CharacterId(Guid.NewGuid()))).Number;

        // Powered on first, and the result checked: blocking is a Messages command now, so on a
        // phone straight out of provisioning it would be refused rather than silently do nothing.
        await Send(new SetPhonePowerCommand(phone.Id, actor, true));
        var blocked = await Send(new BlockNumberCommand(phone.Id, nuisance));
        ExpectCase(blocked is BlockNumberResult.Blocked, "Blocked", blocked);

        var suspended = await Send(new SuspendPhoneCommand(phone.Id, "Police order"));
        ExpectCase(suspended is SuspendPhoneResult.Suspended, "Suspended", suspended);

        var again = await Send(new SuspendPhoneCommand(phone.Id, "Police order"));
        ExpectCase(again is SuspendPhoneResult.AlreadySuspended, "AlreadySuspended", again);

        var restored = await Send(new RestorePhoneCommand(phone.Id));
        ExpectCase(restored is RestorePhoneResult.Restored, "Restored", restored);

        var restoredAgain = await Send(new RestorePhoneCommand(phone.Id));
        ExpectCase(restoredAgain is RestorePhoneResult.NotSuspended, "NotSuspended", restoredAgain);

        var loaded = await Load(phone.Id);
        Assert.Equal(PhoneStatus.Active, loaded.Status);
        Assert.True(loaded.IsBlocked(nuisance));
    }

    [Fact]
    public async Task SuspendedPhone_RefusesEveryAppCommandButKeepsItsState()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var phone = await ProvisionPhone(owner);
        var actor = new PhoneActor(owner);
        await Send(new SetPhonePowerCommand(phone.Id, actor, true));
        await Send(new SuspendPhoneCommand(phone.Id, "Police order"));

        var result = await Send(new ThreadsQuery(phone.Id));
        if (result is not ThreadsResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhoneSuspended, "PhoneSuspended", denied.Reason);
        Assert.True((await Load(phone.Id)).IsPoweredOn);
    }
}
