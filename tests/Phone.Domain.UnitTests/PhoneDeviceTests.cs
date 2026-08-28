using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneDeviceTests
{
    private static readonly PhoneNumber Number = PhoneNumber.Parse("44127788");
    private static readonly PhoneNumber Other = PhoneNumber.Parse("55009911");

    private const string Pin = "1234";

    private static PhoneDevice Phone(CharacterId? owner = null, string pin = Pin) =>
        PhoneDevice.Create(new PhoneDeviceProvisioned(
            new PhoneDeviceId(Guid.NewGuid()), Number, pin, owner ?? new CharacterId(Guid.NewGuid())));

    [Fact]
    public void Create_ProvisionsAnActiveRegisteredPhoneThatIsPoweredOff()
    {
        // Powered off on arrival: a device that woke up already receiving would deliver messages
        // before its owner ever touched it.
        var owner = new CharacterId(Guid.NewGuid());

        var phone = Phone(owner);

        Assert.Equal(Number, phone.Number);
        Assert.Equal(Number.Value, phone.NumberValue);
        Assert.Equal(owner, phone.RegisteredTo);
        Assert.Equal(PhoneStatus.Active, phone.Status);
        Assert.False(phone.IsPoweredOn);
        Assert.Empty(phone.BlockedNumbers);
        Assert.Empty(phone.InstalledApps);
    }

    // --- PIN -------------------------------------------------------------------------------

    [Theory]
    [InlineData("1234")]
    [InlineData("00000000")]
    public void EnsurePin_AcceptsFourToEightDigits(string pin)
    {
        Assert.Equal(pin, PhoneDevice.EnsurePin(pin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    [InlineData("12 4")]
    [InlineData("+1234")]
    public void EnsurePin_RejectsAnythingElse(string? pin)
    {
        Assert.Throws<InvalidPhonePinException>(() => PhoneDevice.EnsurePin(pin));
    }

    [Fact]
    public void HasPin_MatchesTheStoredPinExactly()
    {
        var phone = Phone(pin: "4711");

        Assert.True(phone.HasPin("4711"));
        Assert.False(phone.HasPin("4712"));
    }

    [Fact]
    public void HasPin_ForAnAbsentCandidate_IsFalse()
    {
        // Guards the guard chain: an omitted PIN must never satisfy a comparison by being empty.
        var phone = Phone();

        Assert.False(phone.HasPin(null));
        Assert.False(phone.HasPin(string.Empty));
    }

    [Fact]
    public void ChangePin_ReplacesIt()
    {
        var phone = Phone(pin: "1234");

        phone.ChangePin("9876");

        Assert.True(phone.HasPin("9876"));
        Assert.False(phone.HasPin("1234"));
    }

    [Fact]
    public void ChangePin_ToAnInvalidPin_ThrowsAndLeavesTheOldOneStanding()
    {
        var phone = Phone(pin: "1234");

        Assert.Throws<InvalidPhonePinException>(() => phone.ChangePin("no"));
        Assert.True(phone.HasPin("1234"));
    }

    [Fact]
    public void ChangePin_WhenDeactivated_ThrowsDeactivated()
    {
        var phone = Phone();
        phone.Deactivate();

        Assert.Throws<PhoneDeactivatedException>(() => phone.ChangePin("9876"));
    }

    // --- Power -----------------------------------------------------------------------------

    [Fact]
    public void PowerOn_TurnsThePhoneOn()
    {
        var phone = Phone();

        phone.PowerOn();

        Assert.True(phone.IsPoweredOn);
    }

    [Fact]
    public void PowerOn_WhenAlreadyOn_ThrowsPowerState()
    {
        var phone = Phone();
        phone.PowerOn();

        Assert.Throws<PhoneDevicePowerStateException>(() => phone.PowerOn());
    }

    [Fact]
    public void PowerOff_WhenAlreadyOff_ThrowsPowerState()
    {
        Assert.Throws<PhoneDevicePowerStateException>(() => Phone().PowerOff());
    }

    // --- Apps ------------------------------------------------------------------------------

    [Fact]
    public void InstallApp_RecordsIt()
    {
        var phone = Phone();

        phone.InstallApp(AppKey.Messages);

        Assert.True(phone.HasApp(AppKey.Messages));
    }

    [Fact]
    public void InstallApp_AcceptsEveryAppInTheCatalog()
    {
        // There are no models and no tiers any more: nothing gates one phone's app list against
        // another's.
        var phone = Phone();

        foreach (var key in AppCatalog.Entries.Keys)
        {
            phone.InstallApp(key);
        }

        Assert.Equal(AppCatalog.Entries.Count, phone.InstalledApps.Count);
    }

    [Fact]
    public void InstallApp_Twice_ThrowsAppAlreadyInstalled()
    {
        var phone = Phone();
        phone.InstallApp(AppKey.Messages);

        Assert.Throws<AppAlreadyInstalledException>(() => phone.InstallApp(AppKey.Messages));
    }

    [Fact]
    public void InstallApp_WhenDeactivated_ThrowsDeactivated()
    {
        var phone = Phone();
        phone.Deactivate();

        Assert.Throws<PhoneDeactivatedException>(() => phone.InstallApp(AppKey.Messages));
    }

    [Fact]
    public void UninstallApp_RemovesIt()
    {
        var phone = Phone();
        phone.InstallApp(AppKey.Messages);

        phone.UninstallApp(AppKey.Messages);

        Assert.False(phone.HasApp(AppKey.Messages));
    }

    [Fact]
    public void UninstallApp_ThatIsNotInstalled_ThrowsAppNotInstalled()
    {
        Assert.Throws<AppNotInstalledException>(() => Phone().UninstallApp(AppKey.Messages));
    }

    [Fact]
    public void HasApp_ForAnUninstalledApp_IsFalse()
    {
        Assert.False(Phone().HasApp(AppKey.Messages));
    }

    // --- Enforcement -----------------------------------------------------------------------

    [Fact]
    public void Suspend_WhenActive_SuspendsWithReason()
    {
        var phone = Phone();

        var domainEvent = phone.Suspend("Police order");

        Assert.Equal(PhoneStatus.Suspended, phone.Status);
        Assert.Equal("Police order", domainEvent.Reason);
    }

    [Fact]
    public void Suspend_PreservesAppsAndBlocklist()
    {
        // The whole point of Suspended over Deactivated is that it is reversible with nothing lost.
        var phone = Phone();
        phone.InstallApp(AppKey.Messages);
        phone.Block(Other);

        phone.Suspend("Police order");

        Assert.True(phone.HasApp(AppKey.Messages));
        Assert.Contains(Other, phone.BlockedNumbers);
    }

    [Fact]
    public void Suspend_WhenAlreadySuspended_ThrowsAlreadySuspended()
    {
        var phone = Phone();
        phone.Suspend("Police order");

        Assert.Throws<PhoneAlreadySuspendedException>(() => phone.Suspend("Police order"));
    }

    [Fact]
    public void Suspend_WhenDeactivated_ThrowsDeactivated()
    {
        var phone = Phone();
        phone.Deactivate();

        Assert.Throws<PhoneDeactivatedException>(() => phone.Suspend("Police order"));
    }

    [Fact]
    public void Restore_WhenSuspended_ReturnsToActive()
    {
        var phone = Phone();
        phone.Suspend("Police order");

        phone.Restore();

        Assert.Equal(PhoneStatus.Active, phone.Status);
    }

    [Fact]
    public void Restore_WhenActive_ThrowsNotSuspended()
    {
        Assert.Throws<PhoneNotSuspendedException>(() => Phone().Restore());
    }

    [Fact]
    public void Restore_WhenDeactivated_ThrowsNotSuspended()
    {
        // Deactivation is terminal — an enforcement restore must not resurrect a retired number.
        var phone = Phone();
        phone.Deactivate();

        Assert.Throws<PhoneNotSuspendedException>(() => phone.Restore());
    }

    [Fact]
    public void Deactivate_WhenAlreadyDeactivated_ThrowsDeactivated()
    {
        var phone = Phone();
        phone.Deactivate();

        Assert.Throws<PhoneDeactivatedException>(() => phone.Deactivate());
    }

    // --- Blocklist -------------------------------------------------------------------------

    [Fact]
    public void Block_AddsToBlocklistAndIsReportedByIsBlocked()
    {
        var phone = Phone();

        var domainEvent = phone.Block(Other);

        Assert.Contains(Other, phone.BlockedNumbers);
        Assert.True(phone.IsBlocked(Other));
        Assert.Equal(Other, domainEvent.Number);
    }

    [Fact]
    public void Block_AcceptsAnyFormattingOfTheSameNumber()
    {
        var phone = Phone();
        phone.Block(PhoneNumber.Parse("5500-9911"));

        Assert.True(phone.IsBlocked(PhoneNumber.Parse("+55009911")));
    }

    [Fact]
    public void Block_WhenAlreadyBlocked_ThrowsAlreadyBlocked()
    {
        var phone = Phone();
        phone.Block(Other);

        Assert.Throws<NumberAlreadyBlockedException>(() => phone.Block(Other));
    }

    [Fact]
    public void Block_OwnNumber_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => Phone().Block(Number));
    }

    [Fact]
    public void Unblock_RemovesFromBlocklist()
    {
        var phone = Phone();
        phone.Block(Other);

        phone.Unblock(Other);

        Assert.False(phone.IsBlocked(Other));
    }

    [Fact]
    public void Unblock_WhenNotBlocked_ThrowsNotBlocked()
    {
        Assert.Throws<NumberNotBlockedException>(() => Phone().Unblock(Other));
    }

    [Fact]
    public void IsBlocked_ForAnUnknownNumber_IsFalse()
    {
        Assert.False(Phone().IsBlocked(Other));
    }

    // --- Replay ----------------------------------------------------------------------------

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var phone = new PhoneDevice();

        phone.Apply(new PhoneDeviceProvisioned(phoneId, Number, "1234", new CharacterId(Guid.NewGuid())));
        phone.Apply(new PhoneDevicePoweredOn(phoneId));
        phone.Apply(new PhonePinChanged(phoneId, "9876"));
        phone.Apply(new PhoneNumberBlocked(phoneId, Other));
        phone.Apply(new PhoneSuspended(phoneId, "Police order"));
        phone.Apply(new PhoneRestored(phoneId));
        phone.Apply(new AppInstalled(phoneId, AppKey.Messages));
        phone.Apply(new AppUninstalled(phoneId, AppKey.Messages));

        Assert.Equal(PhoneStatus.Active, phone.Status);
        Assert.True(phone.IsPoweredOn);
        Assert.True(phone.HasPin("9876"));
        Assert.True(phone.IsBlocked(Other));
        Assert.False(phone.HasApp(AppKey.Messages));
    }
}
