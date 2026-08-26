using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Phone.Domain.Sims.Events;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class SimCardTests
{
    private static readonly PhoneNumber Number = PhoneNumber.Parse("44127788");
    private static readonly PhoneNumber Other = PhoneNumber.Parse("55009911");

    private static SimCard CreateSim(CharacterId? owner = null) =>
        SimCard.Create(new SimCardIssued(new SimCardId(Guid.NewGuid()), Number, owner ?? new CharacterId(Guid.NewGuid())));

    private static PhoneDeviceId ADevice() => new(Guid.NewGuid());

    [Fact]
    public void Create_IssuesAnActiveUninstalledSim()
    {
        var owner = new CharacterId(Guid.NewGuid());

        var sim = CreateSim(owner);

        Assert.Equal(Number, sim.Number);
        Assert.Equal(owner, sim.RegisteredTo);
        Assert.Equal(SimCardStatus.Active, sim.Status);
        Assert.Null(sim.InstalledIn);
        Assert.Empty(sim.BlockedNumbers);
    }

    [Fact]
    public void InstallInto_WhenLoose_RecordsTheDevice()
    {
        var sim = CreateSim();
        var device = ADevice();

        var domainEvent = sim.InstallInto(device);

        Assert.Equal(device, sim.InstalledIn);
        Assert.Equal(device, domainEvent.DeviceId);
    }

    [Fact]
    public void InstallInto_WhenAlreadyInstalled_ThrowsAlreadyInstalled()
    {
        var sim = CreateSim();
        sim.InstallInto(ADevice());

        Assert.Throws<SimCardAlreadyInstalledException>(() => sim.InstallInto(ADevice()));
    }

    [Fact]
    public void InstallInto_WhileSuspended_IsAllowed()
    {
        // Suspension is a network-side lock, not a physical one: the card still fits the slot. What
        // it cannot do is send or receive, and PhoneAccessPolicy is what enforces that.
        var sim = CreateSim();
        sim.Suspend("Police order");

        sim.InstallInto(ADevice());

        Assert.NotNull(sim.InstalledIn);
    }

    [Fact]
    public void InstallInto_WhenDeactivated_ThrowsDeactivated()
    {
        var sim = CreateSim();
        sim.Deactivate();

        Assert.Throws<SimCardDeactivatedException>(() => sim.InstallInto(ADevice()));
    }

    [Fact]
    public void Eject_WhenInstalled_ClearsTheDevice()
    {
        var sim = CreateSim();
        var device = ADevice();
        sim.InstallInto(device);

        var domainEvent = sim.Eject();

        Assert.Null(sim.InstalledIn);
        Assert.Equal(device, domainEvent.DeviceId);
    }

    [Fact]
    public void Eject_WhenLoose_ThrowsNotInstalled()
    {
        Assert.Throws<SimCardNotInstalledException>(() => CreateSim().Eject());
    }

    [Fact]
    public void Suspend_WhenActive_SuspendsWithReason()
    {
        var sim = CreateSim();

        var domainEvent = sim.Suspend("Police order");

        Assert.Equal(SimCardStatus.Suspended, sim.Status);
        Assert.Equal("Police order", domainEvent.Reason);
    }

    [Fact]
    public void Suspend_PreservesInstallationAndBlocklist()
    {
        // The whole point of Suspended over Deactivated is that it is reversible with nothing lost.
        var sim = CreateSim();
        var device = ADevice();
        sim.InstallInto(device);
        sim.Block(Other);

        sim.Suspend("Police order");

        Assert.Equal(device, sim.InstalledIn);
        Assert.Contains(Other, sim.BlockedNumbers);
    }

    [Fact]
    public void Suspend_WhenAlreadySuspended_ThrowsAlreadySuspended()
    {
        var sim = CreateSim();
        sim.Suspend("Police order");

        Assert.Throws<SimCardAlreadySuspendedException>(() => sim.Suspend("Police order"));
    }

    [Fact]
    public void Suspend_WhenDeactivated_ThrowsDeactivated()
    {
        var sim = CreateSim();
        sim.Deactivate();

        Assert.Throws<SimCardDeactivatedException>(() => sim.Suspend("Police order"));
    }

    [Fact]
    public void Restore_WhenSuspended_ReturnsToActive()
    {
        var sim = CreateSim();
        sim.Suspend("Police order");

        sim.Restore();

        Assert.Equal(SimCardStatus.Active, sim.Status);
    }

    [Fact]
    public void Restore_WhenActive_ThrowsNotSuspended()
    {
        Assert.Throws<SimCardNotSuspendedException>(() => CreateSim().Restore());
    }

    [Fact]
    public void Restore_WhenDeactivated_ThrowsNotSuspended()
    {
        // Deactivation is terminal — an enforcement restore must not resurrect a retired number.
        var sim = CreateSim();
        sim.Deactivate();

        Assert.Throws<SimCardNotSuspendedException>(() => sim.Restore());
    }

    [Fact]
    public void Deactivate_WhenAlreadyDeactivated_ThrowsDeactivated()
    {
        var sim = CreateSim();
        sim.Deactivate();

        Assert.Throws<SimCardDeactivatedException>(() => sim.Deactivate());
    }

    [Fact]
    public void Block_AddsToBlocklistAndIsReportedByIsBlocked()
    {
        var sim = CreateSim();

        var domainEvent = sim.Block(Other);

        Assert.Contains(Other, sim.BlockedNumbers);
        Assert.True(sim.IsBlocked(Other));
        Assert.Equal(Other, domainEvent.Number);
    }

    [Fact]
    public void Block_AcceptsAnyFormattingOfTheSameNumber()
    {
        var sim = CreateSim();
        sim.Block(PhoneNumber.Parse("5500-9911"));

        Assert.True(sim.IsBlocked(PhoneNumber.Parse("+55009911")));
    }

    [Fact]
    public void Block_WhenAlreadyBlocked_ThrowsAlreadyBlocked()
    {
        var sim = CreateSim();
        sim.Block(Other);

        Assert.Throws<NumberAlreadyBlockedException>(() => sim.Block(Other));
    }

    [Fact]
    public void Block_OwnNumber_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => CreateSim().Block(Number));
    }

    [Fact]
    public void Unblock_RemovesFromBlocklist()
    {
        var sim = CreateSim();
        sim.Block(Other);

        sim.Unblock(Other);

        Assert.False(sim.IsBlocked(Other));
    }

    [Fact]
    public void Unblock_WhenNotBlocked_ThrowsNotBlocked()
    {
        Assert.Throws<NumberNotBlockedException>(() => CreateSim().Unblock(Other));
    }

    [Fact]
    public void IsBlocked_ForAnUnknownNumber_IsFalse()
    {
        Assert.False(CreateSim().IsBlocked(Other));
    }

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var simId = new SimCardId(Guid.NewGuid());
        var device = ADevice();
        var sim = new SimCard();

        sim.Apply(new SimCardIssued(simId, Number, new CharacterId(Guid.NewGuid())));
        sim.Apply(new SimCardInstalled(simId, device));
        sim.Apply(new SimCardNumberBlocked(simId, Other));
        sim.Apply(new SimCardSuspended(simId, "Police order"));
        sim.Apply(new SimCardRestored(simId));

        Assert.Equal(SimCardStatus.Active, sim.Status);
        Assert.Equal(device, sim.InstalledIn);
        Assert.True(sim.IsBlocked(Other));
    }
}
