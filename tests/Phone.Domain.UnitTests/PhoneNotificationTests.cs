using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Notifications;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneNotificationTests
{
    private static readonly PhoneDeviceId Phone = new(Guid.NewGuid());
    private static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // This is the only tier CI runs against `Fit`, since the integration project that exercises the
    // repository and the cap end-to-end needs live Postgres.
    private static List<PhoneNotification> Notifications(int count) =>
        [.. Enumerable.Range(0, count).Select(i => PhoneNotification.Create(
            Phone, AppKey.Messages, "message.received", "thread-1", "55009911", $"message {i}", [], At.AddSeconds(i)))];

    [Fact]
    public void Fit_UnderTheLimit_StoresEverythingAndDeletesNothing()
    {
        var existing = Notifications(2);
        var incoming = Notifications(1);

        var (toStore, toDelete) = PhoneNotification.Fit(existing, incoming, limit: 10);

        Assert.Equal(existing.Concat(incoming), toStore);
        Assert.Empty(toDelete);
    }

    [Fact]
    public void Fit_OverTheLimit_DropsTheOldestExistingFirst()
    {
        var existing = Notifications(3);
        var incoming = Notifications(1);

        var (toStore, toDelete) = PhoneNotification.Fit(existing, incoming, limit: 3);

        // The oldest existing one is evicted; the two newest existing plus the incoming one survive.
        Assert.Equal([existing[1], existing[2], incoming[0]], toStore);
        Assert.Equal([existing[0].Id], toDelete);
    }

    [Fact]
    public void Fit_WhenTheIncomingBatchAloneExceedsTheLimit_DropsTheOldestOfTheBatchToo()
    {
        var existing = Notifications(2);
        var incoming = Notifications(5);

        var (toStore, toDelete) = PhoneNotification.Fit(existing, incoming, limit: 3);

        // Nothing existing survives a batch this large, and only the newest 3 of the batch are kept —
        // a single flush must not grow the queue past the cap just because nothing existing needed
        // evicting.
        Assert.Equal(incoming.TakeLast(3), toStore);
        Assert.Equal(existing.Select(n => n.Id), toDelete);
    }

    [Fact]
    public void Fit_ExactlyAtTheLimit_StoresEverythingAndDeletesNothing()
    {
        var existing = Notifications(2);
        var incoming = Notifications(1);

        var (toStore, toDelete) = PhoneNotification.Fit(existing, incoming, limit: 3);

        Assert.Equal(existing.Concat(incoming), toStore);
        Assert.Empty(toDelete);
    }
}
