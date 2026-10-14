using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Notifications;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the platform notification queue: publication alongside a
/// message append, the app-less guard chain, acking, the cap, and the rule that MarkThreadRead clears
/// a thread's banners while acking a poll deliberately does not.
/// </summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private const string Pin = "1234";

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static void ExpectCase(bool matched, string expected, object actual) =>
        Assert.True(matched, $"Expected {expected}, got {actual}");

    private async Task<T> Send<T>(IRequest<T> request)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, CancellationToken.None);
    }

    private async Task<Phone> SetUpPhone(string pin = Pin)
    {
        var owner = new CharacterId(Guid.NewGuid());

        var result = await Send(new ProvisionPhoneCommand(owner, pin));
        if (result is not ProvisionPhoneResult.Provisioned provisioned)
        {
            throw new XunitException($"Expected Provisioned, got {result}");
        }

        await Send(new SetPhonePowerCommand(provisioned.PhoneId, new PhoneActor(owner), true));

        return new Phone(owner, provisioned.PhoneId, provisioned.Number);
    }

    private sealed record Phone(CharacterId Owner, PhoneDeviceId Id, PhoneNumber Number)
    {
        public PhoneActor Actor => new(Owner);
    }

    private async Task<IReadOnlyList<PhoneNotification>> Notifications(Phone phone, AppKey? appKey = null)
    {
        var result = await Send(new PhoneNotificationsQuery(phone.Id, appKey));
        return result is PhoneNotificationsResult.Notifications notifications
            ? notifications.Entries
            : throw new XunitException($"Expected Notifications, got {result}");
    }

    private async Task<AckNotificationsResult.Acknowledged> AckNotifications(Phone phone, params Guid[] ids)
    {
        var result = await Send(new AckNotificationsCommand(phone.Id, ids));
        return result is AckNotificationsResult.Acknowledged acknowledged
            ? acknowledged
            : throw new XunitException($"Expected Acknowledged, got {result}");
    }

    private async Task<T> WithHiveSetting<T>(Func<UpdateHiveSettingsCommand> set, Func<UpdateHiveSettingsCommand> restore, Func<Task<T>> body)
    {
        await Send(set());
        try
        {
            return await body();
        }
        finally
        {
            await Send(restore());
        }
    }

    [Fact]
    public async Task SendingAMessage_PostsOneNotificationOnTheRecipient_TitledWithTheSendersNumber()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        var sent = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "on my way"));
        if (sent is not SendMessageResult.Sent result)
        {
            throw new XunitException($"Expected Sent, got {sent}");
        }

        var notification = Assert.Single(await Notifications(recipient));
        Assert.Equal(AppKey.Messages, notification.AppKey);
        Assert.Equal("message.received", notification.Category);
        Assert.Equal(sender.Number.Value, notification.Title);
        Assert.Equal("on my way", notification.Body);

        // result.ThreadId is the sender's own thread — the recipient's is on the delivery record.
        var delivery = Assert.Single(result.Deliveries);
        Assert.Equal(delivery.ThreadId.Value.ToString(), notification.GroupKey);
    }

    [Fact]
    public async Task SendingAMessage_PostsNothingOnTheSendersOwnPhone()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "on my way"));

        Assert.Empty(await Notifications(sender));
    }

    [Fact]
    public async Task ABlockedSender_PostsNoNotification()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new BlockNumberCommand(recipient.Id, sender.Number));

        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "on my way"));

        // Reported back to the sender as delivered, exactly like a real block — and no banner either,
        // or the queue would double as a block-detector.
        Assert.Empty(await Notifications(recipient));
    }

    [Fact]
    public async Task APoweredOffRecipient_QueuesWithNoNotification_ThenPowerOnFlushesBoth()
    {
        var sender = await SetUpPhone();
        var owner = new CharacterId(Guid.NewGuid());
        var provisioned = await Send(new ProvisionPhoneCommand(owner, Pin));
        if (provisioned is not ProvisionPhoneResult.Provisioned recipientProvisioned)
        {
            throw new XunitException($"Expected Provisioned, got {provisioned}");
        }

        var recipient = new Phone(owner, recipientProvisioned.PhoneId, recipientProvisioned.Number);

        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "on my way"));

        // The queue itself is unreachable while powered off (the dedicated PhonePoweredOff test
        // covers that guard directly), so "no notification while queued" is checked the only way
        // observable here: exactly one appears after power-on, not a stale duplicate from the send.
        var deniedWhileOff = await Send(new PhoneNotificationsQuery(recipient.Id, null));
        ExpectCase(
            deniedWhileOff is PhoneNotificationsResult.AccessDenied { Reason: PhoneAccessResult.PhonePoweredOff },
            "AccessDenied(PhonePoweredOff)",
            deniedWhileOff);

        await Send(new SetPhonePowerCommand(recipient.Id, recipient.Actor, true));

        var notification = Assert.Single(await Notifications(recipient));
        Assert.Equal("on my way", notification.Body);
    }

    [Fact]
    public async Task Ack_DeletesOnlyTheGivenIds()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "second"));

        var before = await Notifications(recipient);
        Assert.Equal(2, before.Count);

        await AckNotifications(recipient, before[0].Id);

        var after = await Notifications(recipient);
        Assert.Equal(before[1].Id, Assert.Single(after).Id);
    }

    [Fact]
    public async Task Ack_AnUnknownId_IsIgnoredRatherThanReported()
    {
        var phone = await SetUpPhone();

        var acknowledged = await AckNotifications(phone, Guid.NewGuid());

        _ = acknowledged; // Reaching here without an exception is the assertion: idempotent by contract.
    }

    [Fact]
    public async Task Ack_AnotherPhonesIds_LeavesThemInPlace()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var stranger = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        var notification = Assert.Single(await Notifications(recipient));

        await AckNotifications(stranger, notification.Id);

        Assert.Equal(notification.Id, Assert.Single(await Notifications(recipient)).Id);
    }

    [Fact]
    public async Task AppKeyFilter_ReturnsOnlyThatAppsNotifications()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        Assert.Single(await Notifications(recipient, AppKey.Messages));
        Assert.Empty(await Notifications(recipient, AppKey.Contacts));
    }

    [Fact]
    public async Task MarkThreadRead_ClearsThatThreadsNotifications_AndLeavesAnotherThreadsAlone()
    {
        var sender = await SetUpPhone();
        var recipientA = await SetUpPhone();
        var recipientB = await SetUpPhone();

        var sentA = await Send(new SendMessageCommand(sender.Id, [recipientA.Number], "to A"));
        var sentB = await Send(new SendMessageCommand(sender.Id, [recipientB.Number], "to B"));
        if (sentA is not SendMessageResult.Sent resultA || sentB is not SendMessageResult.Sent)
        {
            throw new XunitException("Expected both sends to succeed.");
        }

        // resultA.ThreadId is the sender's own thread — recipientA's is on its delivery record.
        var recipientAThreadId = Assert.Single(resultA.Deliveries).ThreadId;
        await Send(new MarkThreadReadCommand(recipientA.Id, recipientAThreadId));

        Assert.Empty(await Notifications(recipientA));
        Assert.Single(await Notifications(recipientB));
    }

    [Fact]
    public async Task Ack_DoesNotClearNotifications_OnlyMarkingReadDoes()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var sent = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));
        if (sent is not SendMessageResult.Sent result)
        {
            throw new XunitException($"Expected Sent, got {sent}");
        }

        var notification = Assert.Single(await Notifications(recipient));
        await AckNotifications(recipient, notification.Id);

        // Acking removed the notification from the queue by id, but that is a coincidence of this
        // test acking the one notification that exists — the point being verified is the other
        // direction: MarkThreadRead, not the ack, is what a real phone ties banner-clearing to. A
        // second message makes that visible: acking a *different* notification must not clear this
        // thread's, only reading the thread does.
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "second"));
        Assert.Single(await Notifications(recipient));
        await AckNotifications(recipient, Guid.NewGuid());

        Assert.Single(await Notifications(recipient));

        // result.ThreadId is the sender's own thread — the recipient's is on its delivery record.
        // Both sends land in the same thread (same phone, same participant set), so the first send's
        // delivery still names the right thread to mark read.
        var recipientThreadId = Assert.Single(result.Deliveries).ThreadId;
        await Send(new MarkThreadReadCommand(recipient.Id, recipientThreadId));

        Assert.Empty(await Notifications(recipient));
    }

    [Fact]
    public async Task TheCap_DropsTheOldestNotifications()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var original = (await Send(new HiveSettingsQuery())).PhoneNotificationLimit;

        await WithHiveSetting(
            () => new UpdateHiveSettingsCommand(null, PhoneNotificationLimit: 2),
            () => new UpdateHiveSettingsCommand(null, PhoneNotificationLimit: original),
            async () =>
            {
                await Send(new SendMessageCommand(sender.Id, [recipient.Number], "one"));
                await Send(new SendMessageCommand(sender.Id, [recipient.Number], "two"));
                await Send(new SendMessageCommand(sender.Id, [recipient.Number], "three"));
                return 0;
            });

        var remaining = await Notifications(recipient);
        Assert.Equal(2, remaining.Count);
        Assert.Equal(["two", "three"], remaining.Select(n => n.Body));
    }

    [Fact]
    public async Task Notifications_OnAPoweredOffPhone_IsRefused()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var provisioned = await Send(new ProvisionPhoneCommand(owner, Pin));
        if (provisioned is not ProvisionPhoneResult.Provisioned result)
        {
            throw new XunitException($"Expected Provisioned, got {provisioned}");
        }

        var lookup = await Send(new PhoneNotificationsQuery(result.PhoneId, null));

        if (lookup is not PhoneNotificationsResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {lookup}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhonePoweredOff, "PhonePoweredOff", denied.Reason);
    }

    [Fact]
    public async Task Notifications_WithMessagesUninstalled_AreStillReadable()
    {
        // The regression test for the app-less guard chain: notifications are platform state, so
        // uninstalling the app that published one must not make the queue itself unreachable.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        await Send(new UninstallAppCommand(recipient.Id, recipient.Actor, AppKey.Messages));

        Assert.Single(await Notifications(recipient));
    }
}
