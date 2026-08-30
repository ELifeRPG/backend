using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Apps.Contacts;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the two apps end to end: the Contacts book, and the
/// send/deliver path with its blocklist, queueing, enforcement and throttling.
/// </summary>
public sealed class AppCommandTests : IAsyncLifetime
{
    private const string Pin = "1234";

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private async Task<T> Send<T>(IRequest<T> request)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, CancellationToken.None);
    }

    private static void ExpectCase(bool matched, string expected, object actual) =>
        Assert.True(matched, $"Expected {expected}, got {actual}");

    /// <summary>A character with a powered-on phone — the ordinary starting state.</summary>
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

    /// <summary>Provisioned and left powered off, unlike <see cref="SetUpPhone"/>.</summary>
    private async Task<Phone> ProvisionOnly()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var result = await Send(new ProvisionPhoneCommand(owner, Pin));
        if (result is not ProvisionPhoneResult.Provisioned provisioned)
        {
            throw new XunitException($"Expected Provisioned, got {result}");
        }

        return new Phone(owner, provisioned.PhoneId, provisioned.Number);
    }

    private sealed record Phone(CharacterId Owner, PhoneDeviceId Id, PhoneNumber Number)
    {
        public PhoneActor Actor => new(Owner);
    }

    private async Task<IReadOnlyList<MessageThread>> Threads(Phone phone)
    {
        var result = await Send(new ThreadsQuery(phone.Id));
        return result is ThreadsResult.Threads threads
            ? threads.Entries
            : throw new XunitException($"Expected Threads, got {result}");
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

    // ---------- Contacts ----------

    [Fact]
    public async Task SaveContact_ThenReadBack_RoundTrips()
    {
        var phone = await SetUpPhone();
        var other = await SetUpPhone();

        var saved = await Send(new SaveContactCommand(phone.Id, other.Number, "Dispatcher"));
        ExpectCase(saved is SaveContactResult.Saved, "Saved", saved);

        var result = await Send(new ContactsQuery(phone.Id));
        if (result is not ContactsResult.Contacts contacts)
        {
            throw new XunitException($"Expected Contacts, got {result}");
        }

        Assert.Equal("Dispatcher", Assert.Single(contacts.Entries).DisplayName);
    }

    [Fact]
    public async Task SaveContact_AtTheHiveContactLimit_IsRefused()
    {
        // The cap is hive-wide now rather than a number on the handset's model, so exercising it
        // means moving the knob rather than provisioning a lesser phone.
        var phone = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();
        var original = (await Send(new HiveSettingsQuery())).PhoneContactLimit;

        var result = await WithHiveSetting(
            () => new UpdateHiveSettingsCommand(null, PhoneContactLimit: 1),
            () => new UpdateHiveSettingsCommand(null, PhoneContactLimit: original),
            async () =>
            {
                await Send(new SaveContactCommand(phone.Id, first.Number, "One"));
                return await Send(new SaveContactCommand(phone.Id, second.Number, "Two"));
            });

        ExpectCase(result is SaveContactResult.ContactLimitReached, "ContactLimitReached", result);
    }

    [Fact]
    public async Task ContactsApp_WhenUninstalled_IsRefusedByTheGuardChain()
    {
        var phone = await SetUpPhone();
        await Send(new UninstallAppCommand(phone.Id, phone.Actor, AppKey.Contacts));

        var result = await Send(new ContactsQuery(phone.Id));

        if (result is not ContactsResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.AppNotInstalled, "AppNotInstalled", denied.Reason);
    }

    [Fact]
    public async Task Contacts_AreReachableByWhoeverHoldsThePoweredOnHandset()
    {
        // Contacts belong to the handset, so whoever can use the handset can read them. No acting
        // character is named at all: powering the phone on is where possession was proven.
        var phone = await SetUpPhone();
        var other = await SetUpPhone();
        await Send(new SaveContactCommand(phone.Id, other.Number, "Dispatcher"));

        var result = await Send(new ContactsQuery(phone.Id));

        if (result is not ContactsResult.Contacts contacts)
        {
            throw new XunitException($"Expected Contacts, got {result}");
        }

        Assert.Equal("Dispatcher", Assert.Single(contacts.Entries).DisplayName);
    }

    // ---------- Messages ----------

    [Fact]
    public async Task SendMessage_DeliversToBothSidesOfTheConversation()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        var result = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "on my way"));
        ExpectCase(result is SendMessageResult.Sent, "Sent", result);

        var senderThread = Assert.Single(await Threads(sender));
        var recipientThread = Assert.Single(await Threads(recipient));

        Assert.True(Assert.Single(senderThread.Messages).IsOutbound);
        Assert.False(Assert.Single(recipientThread.Messages).IsOutbound);
        Assert.Equal("on my way", recipientThread.Messages[0].Body);
        Assert.Equal(1, recipientThread.UnreadCount);
        Assert.Equal(0, senderThread.UnreadCount);
    }

    [Fact]
    public async Task SendMessage_ToAGroup_GivesEveryoneATheOthersThread()
    {
        var sender = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();

        await Send(new SendMessageCommand(sender.Id, [first.Number, second.Number], "meet at the docks"));

        var senderThread = Assert.Single(await Threads(sender));
        var firstThread = Assert.Single(await Threads(first));

        Assert.Equal(2, senderThread.Participants.Count);
        // From a recipient's side the thread is the sender plus the other recipient.
        Assert.Contains(sender.Number, firstThread.Participants);
        Assert.Contains(second.Number, firstThread.Participants);
        Assert.DoesNotContain(first.Number, firstThread.Participants);
    }

    [Fact]
    public async Task SendMessage_TwiceToTheSamePeople_ReusesOneThread()
    {
        var sender = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();

        await Send(new SendMessageCommand(sender.Id, [first.Number, second.Number], "one"));
        // Same people, opposite order — the thread key must not care.
        await Send(new SendMessageCommand(sender.Id, [second.Number, first.Number], "two"));

        var thread = Assert.Single(await Threads(sender));
        Assert.Equal(2, thread.Messages.Count);
    }

    [Fact]
    public async Task SendMessage_ToABlockedNumber_LooksSentButNeverArrives()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new BlockNumberCommand(recipient.Id, sender.Number));

        var result = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "let me in"));

        // Reported as sent with nothing undeliverable: the sender must not be able to detect a block.
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Empty(sent.UndeliverableRecipients);
        Assert.Single(await Threads(sender));
        Assert.Empty(await Threads(recipient));
    }

    [Fact]
    public async Task SendMessage_ToASuspendedPhone_IsUndeliverableAndIsNotQueued()
    {
        // Enforcement has to block, not delay — so nothing is held for a later restore.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SuspendPhoneCommand(recipient.Id, "Police order"));

        var result = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "you there?"));
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Equal(recipient.Number, Assert.Single(sent.UndeliverableRecipients));

        await Send(new RestorePhoneCommand(recipient.Id));
        Assert.Empty(await Threads(recipient));
    }

    [Fact]
    public async Task SendMessage_ToAnUnknownNumber_IsUndeliverableButStillLandsInTheSendersThread()
    {
        var sender = await SetUpPhone();
        var nobody = PhoneNumber.Parse("99999999");

        var result = await Send(new SendMessageCommand(sender.Id, [nobody], "hello?"));
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Equal(nobody, Assert.Single(sent.UndeliverableRecipients));
        Assert.Single(Assert.Single(await Threads(sender)).Messages);
    }

    [Fact]
    public async Task SendMessage_ToAPoweredOffPhone_QueuesUntilItPowersOn()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SetPhonePowerCommand(recipient.Id, recipient.Actor, false));

        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "call me"));

        await Send(new SetPhonePowerCommand(recipient.Id, recipient.Actor, true));

        var thread = Assert.Single(await Threads(recipient));
        Assert.Equal("call me", Assert.Single(thread.Messages).Body);
    }

    [Fact]
    public async Task SendMessage_WithMessagesUninstalled_QueuesUntilItIsInstalledAgain()
    {
        // The second of the two moments a number becomes reachable again, and the replacement for
        // what seating a SIM used to do.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new UninstallAppCommand(recipient.Id, recipient.Actor, AppKey.Messages));

        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "where is your phone"));

        await Send(new InstallAppCommand(recipient.Id, recipient.Actor, AppKey.Messages));

        var thread = Assert.Single(await Threads(recipient));
        Assert.Equal("where is your phone", Assert.Single(thread.Messages).Body);
    }

    [Fact]
    public async Task MessageHistory_IsTrimmedOnTheNextArrivalAfterTheHiveLimitDrops()
    {
        // Trimming happens on append against the cap of that moment, and the cap rides on the event
        // so a replay rebuilds the history that existed. Lowering the hive knob therefore costs a
        // thread its backlog on its next message rather than at once.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var original = (await Send(new HiveSettingsQuery())).PhoneThreadMessageLimit;

        foreach (var body in (string[])["one", "two", "three"])
        {
            await Send(new SendMessageCommand(sender.Id, [recipient.Number], body));
        }

        Assert.Equal(3, Assert.Single(await Threads(recipient)).Messages.Count);

        var trimmed = await WithHiveSetting(
            () => new UpdateHiveSettingsCommand(null, PhoneThreadMessageLimit: 2),
            () => new UpdateHiveSettingsCommand(null, PhoneThreadMessageLimit: original),
            async () =>
            {
                await Send(new SendMessageCommand(sender.Id, [recipient.Number], "four"));
                return Assert.Single(await Threads(recipient));
            });

        Assert.Equal(["three", "four"], trimmed.Messages.Select(message => message.Body));
    }

    [Fact]
    public async Task MarkThreadRead_ClearsTheUnreadCount()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "hey"));

        var thread = Assert.Single(await Threads(recipient));
        var marked = await Send(new MarkThreadReadCommand(recipient.Id, thread.Id));
        ExpectCase(marked is MarkThreadReadResult.MarkedRead, "MarkedRead", marked);

        Assert.Equal(0, Assert.Single(await Threads(recipient)).UnreadCount);
    }

    // ---------- Message polling ----------

    /// <summary>The mod polls rather than holding a hub connection — Reforger cannot consume SignalR.</summary>
    private async Task<MessageUpdatesResult.Updates> Poll(Phone phone, DateTimeOffset? since)
    {
        var result = await Send(new MessageUpdatesQuery(phone.Id, since));
        return result is MessageUpdatesResult.Updates updates
            ? updates
            : throw new XunitException($"Expected Updates, got {result}");
    }

    [Fact]
    public async Task MessageUpdates_WithNoCursor_ReturnsEveryThreadWhole()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        var updates = await Poll(recipient, since: null);

        Assert.Equal("first", Assert.Single(Assert.Single(updates.Threads).NewMessages).Body);
    }

    [Fact]
    public async Task MessageUpdates_WithTheCursorFromAnEarlierPoll_ReturnsOnlyWhatArrivedSince()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        var first = await Poll(recipient, since: null);
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "second"));

        var second = await Poll(recipient, since: first.PolledAt);

        // The thread is the same one; only the message that arrived after the cursor comes back.
        Assert.Equal("second", Assert.Single(Assert.Single(second.Threads).NewMessages).Body);
    }

    [Fact]
    public async Task MessageUpdates_WithNothingNewSinceTheCursor_ReturnsNoThreads()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "first"));

        var first = await Poll(recipient, since: null);
        var second = await Poll(recipient, since: first.PolledAt);

        Assert.Empty(second.Threads);
    }

    [Fact]
    public async Task MessageUpdates_OnAPoweredOffPhone_IsRefusedLikeAnyAppOperation()
    {
        var phone = await ProvisionOnly();

        var result = await Send(new MessageUpdatesQuery(phone.Id, null));

        if (result is not MessageUpdatesResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhonePoweredOff, "PhonePoweredOff", denied.Reason);
    }

    [Fact]
    public async Task Thread_BelongingToAnotherPhone_ReadsAsNotFound()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.Id, [recipient.Number], "hey"));
        var recipientThread = Assert.Single(await Threads(recipient));

        var result = await Send(new ThreadQuery(sender.Id, recipientThread.Id));

        ExpectCase(result is ThreadResult.NotFound, "NotFound", result);
    }

    [Fact]
    public async Task SendMessage_FromASuspendedPhone_IsRefused()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SuspendPhoneCommand(sender.Id, "Police order"));

        var result = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "still here"));

        if (result is not SendMessageResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhoneSuspended, "PhoneSuspended", denied.Reason);
    }

    [Fact]
    public async Task SendMessage_ByAStrangerOnAPoweredOnPhone_SendsFromItsNumber()
    {
        // Possession is proven once, at power-on — an app operation inherits it from the power state
        // instead of re-checking the actor. A powered-on handset is a usable handset, whoever holds it.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        var result = await Send(new SendMessageCommand(
            sender.Id, [recipient.Number], "borrowed this"));

        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        // Someone else's handset, its number on the message — what makes a looted phone worth
        // carrying now that the biolock is gone.
        Assert.Equal(sender.Number, sent.From);
        Assert.Equal("borrowed this", Assert.Single(Assert.Single(await Threads(recipient)).Messages).Body);
    }

    [Fact]
    public async Task SendMessage_AddressedOnlyToYourself_HasNoRecipients()
    {
        var sender = await SetUpPhone();

        var result = await Send(new SendMessageCommand(sender.Id, [sender.Number], "note to self"));

        ExpectCase(result is SendMessageResult.NoRecipients, "NoRecipients", result);
    }

    [Fact]
    public async Task SendMessage_BeyondTheHiveBodyLimit_IsRefused()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var settings = await Send(new HiveSettingsQuery());

        var result = await Send(new SendMessageCommand(
            sender.Id, [recipient.Number], new string('x', settings.SmsMaxBodyLength + 1)));

        ExpectCase(result is SendMessageResult.BodyTooLong, "BodyTooLong", result);
    }

    [Fact]
    public async Task SendMessage_BeyondTheHiveGroupLimit_IsRefused()
    {
        var sender = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();
        var original = (await Send(new HiveSettingsQuery())).PhoneMaxGroupParticipants;

        var result = await WithHiveSetting(
            () => new UpdateHiveSettingsCommand(null, PhoneMaxGroupParticipants: 2),
            () => new UpdateHiveSettingsCommand(null, PhoneMaxGroupParticipants: original),
            async () =>
            {
                var third = await SetUpPhone();
                return await Send(new SendMessageCommand(
                    sender.Id, [first.Number, second.Number, third.Number], "too many"));
            });

        ExpectCase(result is SendMessageResult.TooManyRecipients, "TooManyRecipients", result);
    }

    [Fact]
    public async Task SendMessage_BeyondThePerMinuteQuota_IsRateLimited()
    {
        var original = (await Send(new HiveSettingsQuery())).SmsPerMinutePerPhone;

        await WithHiveSetting(
            () => new UpdateHiveSettingsCommand(null, SmsPerMinutePerPhone: 1),
            () => new UpdateHiveSettingsCommand(null, SmsPerMinutePerPhone: original),
            async () =>
            {
                var sender = await SetUpPhone();
                var recipient = await SetUpPhone();

                var first = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "one"));
                ExpectCase(first is SendMessageResult.Sent, "Sent", first);

                var second = await Send(new SendMessageCommand(sender.Id, [recipient.Number], "two"));
                ExpectCase(second is SendMessageResult.RateLimited, "RateLimited", second);
                return true;
            });
    }
    // ---------- Blocklist (Messages) ----------

    [Fact]
    public async Task BlockNumber_ThenUnblock_RoundTrips()
    {
        var phone = await SetUpPhone();
        var nuisance = (await SetUpPhone()).Number;

        var blocked = await Send(new BlockNumberCommand(phone.Id, nuisance));
        ExpectCase(blocked is BlockNumberResult.Blocked, "Blocked", blocked);

        var again = await Send(new BlockNumberCommand(phone.Id, nuisance));
        ExpectCase(again is BlockNumberResult.AlreadyBlocked, "AlreadyBlocked", again);

        var unblocked = await Send(new UnblockNumberCommand(phone.Id, nuisance));
        ExpectCase(unblocked is UnblockNumberResult.Unblocked, "Unblocked", unblocked);

        var unblockedAgain = await Send(new UnblockNumberCommand(phone.Id, nuisance));
        ExpectCase(unblockedAgain is UnblockNumberResult.NotBlocked, "NotBlocked", unblockedAgain);
    }

    [Fact]
    public async Task BlockNumber_AgainstOwnNumber_IsRefused()
    {
        var phone = await SetUpPhone();

        var result = await Send(new BlockNumberCommand(phone.Id, phone.Number));

        ExpectCase(result is BlockNumberResult.CannotBlockOwnNumber, "CannotBlockOwnNumber", result);
    }

    [Fact]
    public async Task BlockNumber_OnAPoweredOffPhone_IsRefused()
    {
        // The blocklist is the Messages app's, so editing it runs the same guard chain as a send.
        // This used to be allowed, back when blocking was a platform command reachable on a dead
        // handset; moving the route under /apps/messages/ is what changed it.
        var phone = await ProvisionOnly();
        var nuisance = (await SetUpPhone()).Number;

        var result = await Send(new BlockNumberCommand(phone.Id, nuisance));

        if (result is not BlockNumberResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhonePoweredOff, "PhonePoweredOff", denied.Reason);
    }

    [Fact]
    public async Task BlockNumber_WithMessagesUninstalled_IsRefused()
    {
        var phone = await SetUpPhone();
        var nuisance = (await SetUpPhone()).Number;
        await Send(new UninstallAppCommand(phone.Id, phone.Actor, AppKey.Messages));

        var result = await Send(new BlockNumberCommand(phone.Id, nuisance));

        if (result is not BlockNumberResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.AppNotInstalled, "AppNotInstalled", denied.Reason);
    }

    [Fact]
    public async Task Blocklist_SurvivesUninstallingAndReinstallingMessages()
    {
        // Only the guard moved into the app; the list itself still lives on the phone, so an
        // uninstall does not clear it.
        var phone = await SetUpPhone();
        var nuisance = await SetUpPhone();
        await Send(new BlockNumberCommand(phone.Id, nuisance.Number));

        await Send(new UninstallAppCommand(phone.Id, phone.Actor, AppKey.Messages));
        await Send(new InstallAppCommand(phone.Id, phone.Actor, AppKey.Messages));

        var stillBlocked = await Send(new BlockNumberCommand(phone.Id, nuisance.Number));
        ExpectCase(stillBlocked is BlockNumberResult.AlreadyBlocked, "AlreadyBlocked", stillBlocked);
    }
}
