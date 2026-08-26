using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Apps.Contacts;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Application.Sims;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the two apps end to end: the Contacts book that travels
/// with a SIM, and the send/deliver path with its blocking, queueing, enforcement and throttling.
/// </summary>
public sealed class AppCommandTests : IAsyncLifetime
{
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

    private async Task<PhoneModelId> CreateModel(string name, int simSlots = 1, int contactLimit = 50, int threadMessageLimit = 30)
    {
        var result = await Send(new CreatePhoneModelCommand(
            name, 1, null, simSlots, [AppKey.Messages, AppKey.Contacts], contactLimit, threadMessageLimit, 5));
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

    /// <summary>A character with a powered-on handset holding one SIM — the ordinary starting state.</summary>
    private async Task<(CharacterId Owner, PhoneDeviceId DeviceId, SimCardId SimId, PhoneNumber Number)> SetUpPhone(
        int simSlots = 1, int contactLimit = 50, int threadMessageLimit = 30, AppKey[]? apps = null)
    {
        var owner = new CharacterId(Guid.NewGuid());

        var modelResult = await Send(new CreatePhoneModelCommand(
            "Test handset", 1, null, simSlots, apps ?? [AppKey.Messages, AppKey.Contacts], contactLimit, threadMessageLimit, 5));
        if (modelResult is not CreatePhoneModelResult.Created model)
        {
            throw new XunitException($"Expected Created, got {modelResult}");
        }

        var deviceResult = await Send(new ProvisionPhoneDeviceCommand(owner, model.ModelId));
        if (deviceResult is not ProvisionPhoneDeviceResult.Provisioned device)
        {
            throw new XunitException($"Expected Provisioned, got {deviceResult}");
        }

        var simResult = await Send(new ProvisionSimCardCommand(owner));
        if (simResult is not ProvisionSimCardResult.Provisioned sim)
        {
            throw new XunitException($"Expected Provisioned, got {simResult}");
        }

        await Send(new InstallSimCommand(device.DeviceId, sim.SimCardId, owner));
        await Send(new SetPhonePowerCommand(device.DeviceId, owner, true));

        return (owner, device.DeviceId, sim.SimCardId, sim.Number);
    }

    private async Task<IReadOnlyList<MessageThread>> Threads(SimCardId simId, CharacterId owner)
    {
        var result = await Send(new ThreadsQuery(simId, owner));
        return result is ThreadsResult.Threads threads
            ? threads.Entries
            : throw new XunitException($"Expected Threads, got {result}");
    }

    // ---------- Contacts ----------

    [Fact]
    public async Task SaveContact_ThenReadBack_RoundTrips()
    {
        var phone = await SetUpPhone();
        var other = await SetUpPhone();

        var saved = await Send(new SaveContactCommand(phone.SimId, phone.Owner, other.Number, "Dispatcher"));
        ExpectCase(saved is SaveContactResult.Saved, "Saved", saved);

        var result = await Send(new ContactsQuery(phone.SimId, phone.Owner));
        if (result is not ContactsResult.Contacts contacts)
        {
            throw new XunitException($"Expected Contacts, got {result}");
        }

        Assert.Equal("Dispatcher", Assert.Single(contacts.Entries).DisplayName);
    }

    [Fact]
    public async Task SaveContact_AtTheModelLimit_IsRefused()
    {
        var phone = await SetUpPhone(contactLimit: 1);
        var first = await SetUpPhone();
        var second = await SetUpPhone();

        await Send(new SaveContactCommand(phone.SimId, phone.Owner, first.Number, "One"));
        var result = await Send(new SaveContactCommand(phone.SimId, phone.Owner, second.Number, "Two"));

        ExpectCase(result is SaveContactResult.ContactLimitReached, "ContactLimitReached", result);
    }

    [Fact]
    public async Task Contacts_TravelWithTheSimIntoAnotherHandset()
    {
        // The whole reason contacts live on the SIM rather than the device.
        var phone = await SetUpPhone();
        var other = await SetUpPhone();
        await Send(new SaveContactCommand(phone.SimId, phone.Owner, other.Number, "Dispatcher"));

        var secondDeviceId = await ProvisionDevice(await CreateModel("Second handset"), phone.Owner);

        await Send(new EjectSimCommand(phone.DeviceId, phone.SimId, phone.Owner));
        await Send(new InstallSimCommand(secondDeviceId, phone.SimId, phone.Owner));
        await Send(new SetPhonePowerCommand(secondDeviceId, phone.Owner, true));

        var result = await Send(new ContactsQuery(phone.SimId, phone.Owner));
        if (result is not ContactsResult.Contacts contacts)
        {
            throw new XunitException($"Expected Contacts, got {result}");
        }

        Assert.Equal("Dispatcher", Assert.Single(contacts.Entries).DisplayName);
    }

    [Fact]
    public async Task ContactsApp_WhenUninstalled_IsRefusedByTheGuardChain()
    {
        var phone = await SetUpPhone();
        await Send(new UninstallAppCommand(phone.DeviceId, phone.Owner, AppKey.Contacts));

        var result = await Send(new ContactsQuery(phone.SimId, phone.Owner));

        ExpectCase(result is ContactsResult.AccessDenied, "AccessDenied", result);
        if (result is ContactsResult.AccessDenied denied)
        {
            ExpectCase(denied.Reason is PhoneAccessResult.AppNotInstalled, "AppNotInstalled", denied.Reason);
        }
    }

    // ---------- Messages ----------

    [Fact]
    public async Task SendMessage_DeliversToBothSidesOfTheConversation()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "on my way"));
        ExpectCase(result is SendMessageResult.Sent, "Sent", result);

        var senderThread = Assert.Single(await Threads(sender.SimId, sender.Owner));
        var recipientThread = Assert.Single(await Threads(recipient.SimId, recipient.Owner));

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

        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [first.Number, second.Number], "meet at the docks"));

        var senderThread = Assert.Single(await Threads(sender.SimId, sender.Owner));
        var firstThread = Assert.Single(await Threads(first.SimId, first.Owner));

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

        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [first.Number, second.Number], "one"));
        // Same people, opposite order — the thread key must not care.
        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [second.Number, first.Number], "two"));

        var thread = Assert.Single(await Threads(sender.SimId, sender.Owner));
        Assert.Equal(2, thread.Messages.Count);
    }

    [Fact]
    public async Task SendMessage_ToABlockedNumber_LooksSentButNeverArrives()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new BlockNumberCommand(recipient.SimId, recipient.Owner, sender.Number));

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "let me in"));

        // Reported as sent with nothing undeliverable: the sender must not be able to detect a block.
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Empty(sent.UndeliverableRecipients);
        Assert.Single(await Threads(sender.SimId, sender.Owner));
        Assert.Empty(await Threads(recipient.SimId, recipient.Owner));
    }

    [Fact]
    public async Task SendMessage_ToASuspendedSim_IsUndeliverableAndIsNotQueued()
    {
        // Enforcement has to block, not delay — so nothing is held for a later restore.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SuspendSimCommand(recipient.SimId, "Police order"));

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "you there?"));
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Equal(recipient.Number, Assert.Single(sent.UndeliverableRecipients));

        await Send(new RestoreSimCommand(recipient.SimId));
        Assert.Empty(await Threads(recipient.SimId, recipient.Owner));
    }

    [Fact]
    public async Task SendMessage_ToAnUnknownNumber_IsUndeliverableButStillLandsInTheSendersThread()
    {
        var sender = await SetUpPhone();
        var nobody = PhoneNumber.Parse("99999999");

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [nobody], "hello?"));
        if (result is not SendMessageResult.Sent sent)
        {
            throw new XunitException($"Expected Sent, got {result}");
        }

        Assert.Equal(nobody, Assert.Single(sent.UndeliverableRecipients));
        Assert.Single(Assert.Single(await Threads(sender.SimId, sender.Owner)).Messages);
    }

    [Fact]
    public async Task SendMessage_ToAPoweredOffHandset_QueuesUntilItPowersOn()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SetPhonePowerCommand(recipient.DeviceId, recipient.Owner, false));

        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "call me"));

        await Send(new SetPhonePowerCommand(recipient.DeviceId, recipient.Owner, true));

        var thread = Assert.Single(await Threads(recipient.SimId, recipient.Owner));
        Assert.Equal("call me", Assert.Single(thread.Messages).Body);
    }

    [Fact]
    public async Task SendMessage_ToALooseSim_QueuesUntilItIsSeated()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new EjectSimCommand(recipient.DeviceId, recipient.SimId, recipient.Owner));

        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "where is your phone"));

        await Send(new InstallSimCommand(recipient.DeviceId, recipient.SimId, recipient.Owner));

        var thread = Assert.Single(await Threads(recipient.SimId, recipient.Owner));
        Assert.Equal("where is your phone", Assert.Single(thread.Messages).Body);
    }

    [Fact]
    public async Task MessageHistory_TravelsWithTheSimAndIsTrimmedByTheNewHandset()
    {
        // History lives on the SIM, but the cap comes from the handset it is currently in — so
        // dropping a smartphone SIM into a burner costs you the backlog on the next message.
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone(threadMessageLimit: 30);

        foreach (var body in (string[])["one", "two", "three"])
        {
            await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], body));
        }

        Assert.Equal(3, Assert.Single(await Threads(recipient.SimId, recipient.Owner)).Messages.Count);

        var burnerDeviceId = await ProvisionDevice(await CreateModel("Burner", threadMessageLimit: 2), recipient.Owner);

        await Send(new EjectSimCommand(recipient.DeviceId, recipient.SimId, recipient.Owner));
        await Send(new InstallSimCommand(burnerDeviceId, recipient.SimId, recipient.Owner));
        await Send(new SetPhonePowerCommand(burnerDeviceId, recipient.Owner, true));

        // History came across intact...
        Assert.Equal(3, Assert.Single(await Threads(recipient.SimId, recipient.Owner)).Messages.Count);

        // ...and the burner's limit bites on the next arrival.
        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "four"));

        var trimmed = Assert.Single(await Threads(recipient.SimId, recipient.Owner));
        Assert.Equal(["three", "four"], trimmed.Messages.Select(message => message.Body));
    }

    [Fact]
    public async Task MarkThreadRead_ClearsTheUnreadCount()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "hey"));

        var thread = Assert.Single(await Threads(recipient.SimId, recipient.Owner));
        var marked = await Send(new MarkThreadReadCommand(recipient.SimId, recipient.Owner, thread.Id));
        ExpectCase(marked is MarkThreadReadResult.MarkedRead, "MarkedRead", marked);

        Assert.Equal(0, Assert.Single(await Threads(recipient.SimId, recipient.Owner)).UnreadCount);
    }

    [Fact]
    public async Task Thread_BelongingToAnotherSim_ReadsAsNotFound()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "hey"));
        var recipientThread = Assert.Single(await Threads(recipient.SimId, recipient.Owner));

        var result = await Send(new ThreadQuery(sender.SimId, sender.Owner, recipientThread.Id));

        ExpectCase(result is ThreadResult.NotFound, "NotFound", result);
    }

    [Fact]
    public async Task SendMessage_FromASuspendedSim_IsRefused()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        await Send(new SuspendSimCommand(sender.SimId, "Police order"));

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "still here"));

        ExpectCase(result is SendMessageResult.AccessDenied, "AccessDenied", result);
        if (result is SendMessageResult.AccessDenied denied)
        {
            ExpectCase(denied.Reason is PhoneAccessResult.SimSuspended, "SimSuspended", denied.Reason);
        }
    }

    [Fact]
    public async Task SendMessage_ByACharacterWhoOwnsNeither_IsRefused()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();

        var result = await Send(new SendMessageCommand(sender.SimId, recipient.Owner, [recipient.Number], "not mine"));

        ExpectCase(result is SendMessageResult.AccessDenied, "AccessDenied", result);
        if (result is SendMessageResult.AccessDenied denied)
        {
            ExpectCase(denied.Reason is PhoneAccessResult.NotSimOwner, "NotSimOwner", denied.Reason);
        }
    }

    [Fact]
    public async Task SendMessage_AddressedOnlyToYourself_HasNoRecipients()
    {
        var sender = await SetUpPhone();

        var result = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [sender.Number], "note to self"));

        ExpectCase(result is SendMessageResult.NoRecipients, "NoRecipients", result);
    }

    [Fact]
    public async Task SendMessage_BeyondTheHiveBodyLimit_IsRefused()
    {
        var sender = await SetUpPhone();
        var recipient = await SetUpPhone();
        var settings = await Send(new HiveSettingsQuery());

        var result = await Send(new SendMessageCommand(
            sender.SimId, sender.Owner, [recipient.Number], new string('x', settings.SmsMaxBodyLength + 1)));

        ExpectCase(result is SendMessageResult.BodyTooLong, "BodyTooLong", result);
    }

    [Fact]
    public async Task SendMessage_BeyondThePerMinuteQuota_IsRateLimited()
    {
        var original = (await Send(new HiveSettingsQuery())).SmsPerMinutePerSim;
        try
        {
            await Send(new UpdateHiveSettingsCommand(null, SmsPerMinutePerSim: 1));

            var sender = await SetUpPhone();
            var recipient = await SetUpPhone();

            var first = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "one"));
            ExpectCase(first is SendMessageResult.Sent, "Sent", first);

            var second = await Send(new SendMessageCommand(sender.SimId, sender.Owner, [recipient.Number], "two"));
            ExpectCase(second is SendMessageResult.RateLimited, "RateLimited", second);
        }
        finally
        {
            await Send(new UpdateHiveSettingsCommand(null, SmsPerMinutePerSim: original));
        }
    }
}
