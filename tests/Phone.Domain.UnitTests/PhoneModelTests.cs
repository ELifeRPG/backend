using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneModelTests
{
    private static PhoneModelCreated Define(
        int simSlots = 1,
        int contactLimit = 50,
        int threadMessageLimit = 30,
        int maxGroupParticipants = 5,
        AppKey[]? supportedApps = null) =>
        PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()),
            "Burner",
            tier: 1,
            itemId: new ItemId(Guid.NewGuid()),
            simSlots: simSlots,
            supportedApps: supportedApps ?? [AppKey.Messages, AppKey.Contacts],
            contactLimit: contactLimit,
            threadMessageLimit: threadMessageLimit,
            maxGroupParticipants: maxGroupParticipants);

    [Fact]
    public void Define_WithValidValues_ProducesACreatedEvent()
    {
        var domainEvent = Define();

        var model = PhoneModel.Create(domainEvent);

        Assert.Equal("Burner", model.DisplayName);
        Assert.Equal(1, model.Tier);
        Assert.Equal(1, model.SimSlots);
        Assert.Equal(50, model.ContactLimit);
        Assert.Equal(30, model.ThreadMessageLimit);
        Assert.Equal(5, model.MaxGroupParticipants);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Define_WithoutASimSlot_ThrowsArgumentOutOfRange(int simSlots)
    {
        // A device with no slot could never carry a number, so it could never do anything at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => Define(simSlots: simSlots));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Define_WithNonPositiveThreadMessageLimit_ThrowsArgumentOutOfRange(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Define(threadMessageLimit: limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Define_WithNonPositiveContactLimit_ThrowsArgumentOutOfRange(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Define(contactLimit: limit));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Define_WithFewerThanTwoGroupParticipants_ThrowsArgumentOutOfRange(int limit)
    {
        // Two is the floor because a "group" of one participant is just a 1:1 thread.
        Assert.Throws<ArgumentOutOfRangeException>(() => Define(maxGroupParticipants: limit));
    }

    [Fact]
    public void Define_WithBlankDisplayName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()), "  ", 1, null, 1, [AppKey.Messages], 50, 30, 5));
    }

    [Fact]
    public void Define_WithDuplicateSupportedApps_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Define(supportedApps: [AppKey.Messages, AppKey.Messages]));
    }

    [Fact]
    public void Define_WithAnUndefinedApp_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Define(supportedApps: [(AppKey)999]));
    }

    [Fact]
    public void Supports_ReflectsTheSupportedAppList()
    {
        var model = PhoneModel.Create(Define(supportedApps: [AppKey.Messages]));

        Assert.True(model.Supports(AppKey.Messages));
        Assert.False(model.Supports(AppKey.Contacts));
    }
}
