using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). These cover the persistence
/// mechanics the domain tests can not: that Marten can index and query the value types this module
/// leans on, and that projections rebuild the aggregates faithfully.
/// </summary>
public sealed class PhonePersistenceTests : IAsyncLifetime
{
    private const int RetentionLimit = 30;
    private const int MaxGroupParticipants = 5;

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // Every test needs a number no other test has used: the unique index is real, and the schema is
    // shared across the whole run.
    private static PhoneNumber UniqueNumber() =>
        PhoneNumber.Parse(Random.Shared.NextInt64(10_000_000, 99_999_999).ToString());

    [Fact]
    public async Task Phone_RoundTripsIncludingTheNumberValueObjectAndThePin()
    {
        var number = UniqueNumber();
        var owner = new CharacterId(Guid.NewGuid());
        var phoneId = await ProvisionPhone(number, owner, pin: "4711");

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
        var reloaded = await repository.FindByIdAsync(phoneId, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(number, reloaded.Number);
        Assert.Equal(number.Value, reloaded.NumberValue);
        Assert.Equal(owner, reloaded.RegisteredTo);
        Assert.Equal(PhoneStatus.Active, reloaded.Status);

        // The PIN has to survive the round trip, or a reloaded phone would refuse the very holder it
        // was provisioned for.
        Assert.True(reloaded.HasPin("4711"));
    }

    [Fact]
    public async Task Phone_IsFoundByItsNumber()
    {
        var number = UniqueNumber();
        var phoneId = await ProvisionPhone(number, new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();

        // Parsed from a differently formatted spelling on purpose: routing must not care.
        var found = await repository.FindByNumberAsync(PhoneNumber.Parse($"+{number.Value}"), CancellationToken.None);

        Assert.Equal(phoneId, found?.Id);
    }

    [Fact]
    public async Task Phone_WithADuplicateNumber_IsRejectedByTheUniqueIndex()
    {
        var number = UniqueNumber();
        await ProvisionPhone(number, new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
        var provisioned = new PhoneDeviceProvisioned(
            new PhoneDeviceId(Guid.NewGuid()), number, "1234", new CharacterId(Guid.NewGuid()));
        repository.StartStream(PhoneDevice.Create(provisioned), provisioned);

        // The repository translates the unique-index violation into a domain exception so
        // Phone.Application can retry a fresh number without referencing Marten or Npgsql.
        await Assert.ThrowsAsync<PhoneNumberTakenException>(
            async () => await repository.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Phone_AppendedEventsAreProjectedOntoTheDocument()
    {
        var blocked = UniqueNumber();
        var phoneId = await ProvisionPhone(UniqueNumber(), new CharacterId(Guid.NewGuid()));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
            var phone = await repository.FindByIdAsync(phoneId, CancellationToken.None);
            repository.Append(phoneId, phone!.Block(blocked));
            repository.Append(phoneId, phone.InstallApp(AppKey.Messages));
            repository.Append(phoneId, phone.ChangePin("9876"));
            repository.Append(phoneId, phone.Suspend("Police order"));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
            var reloaded = await repository.FindByIdAsync(phoneId, CancellationToken.None);

            Assert.Equal(PhoneStatus.Suspended, reloaded!.Status);
            Assert.True(reloaded.IsBlocked(blocked));
            Assert.True(reloaded.HasApp(AppKey.Messages));
            Assert.True(reloaded.HasPin("9876"));
        }
    }

    [Fact]
    public async Task Phones_AreFoundByRegisteredCharacter()
    {
        var owner = new CharacterId(Guid.NewGuid());
        await ProvisionPhone(UniqueNumber(), owner);
        await ProvisionPhone(UniqueNumber(), owner);
        await ProvisionPhone(UniqueNumber(), new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();

        Assert.Equal(2, (await repository.FindByCharacterAsync(owner, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ContactBook_IsFoundByPhoneAndKeepsItsContacts()
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var bookId = new ContactBookId(Guid.NewGuid());
        var saved = UniqueNumber();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IContactBookRepository>();
            var opened = new ContactBookOpened(bookId, phoneId);
            var book = ContactBook.Create(opened);
            repository.StartStream(book, opened);
            repository.Append(bookId, book.SaveContact(new ContactId(Guid.NewGuid()), saved, "Dispatcher", 50));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IContactBookRepository>();
            var reloaded = await repository.FindByPhoneAsync(phoneId, CancellationToken.None);

            Assert.Equal(bookId, reloaded!.Id);
            Assert.Equal("Dispatcher", reloaded.Find(saved)?.DisplayName);
        }
    }

    [Fact]
    public async Task MessageThread_IsFoundByItsPhoneAndThreadKey()
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var participant = UniqueNumber();
        var owner = UniqueNumber();
        var started = MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), phoneId, owner, [participant], MaxGroupParticipants);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var thread = MessageThread.Create(started);
            repository.StartStream(thread, started);
            repository.Append(thread.Id, thread.RecordInbound(
                new MessageId(Guid.NewGuid()), participant, "where are you", DateTimeOffset.UtcNow, RetentionLimit));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();

            var byKey = await repository.FindByKeyAsync(phoneId, started.ThreadKey, CancellationToken.None);
            var byPhone = await repository.FindByPhoneAsync(phoneId, CancellationToken.None);

            Assert.Equal(started.Id, byKey?.Id);
            Assert.Equal("where are you", Assert.Single(byKey!.Messages).Body);
            Assert.Equal(1, byKey.UnreadCount);
            Assert.Single(byPhone);
        }
    }

    [Fact]
    public async Task MessageThread_RetentionLimitIsEnforcedAcrossAReload()
    {
        // Proves the trim survives persistence, not just in-memory replay: the limit rides on each
        // event, so the projection applies it the same way the aggregate did.
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var participant = UniqueNumber();
        var started = MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), phoneId, UniqueNumber(), [participant], MaxGroupParticipants);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var thread = MessageThread.Create(started);
            repository.StartStream(thread, started);
            foreach (var body in (string[])["one", "two", "three"])
            {
                repository.Append(thread.Id, thread.RecordInbound(
                    new MessageId(Guid.NewGuid()), participant, body, DateTimeOffset.UtcNow, retentionLimit: 2));
            }

            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var reloaded = await repository.FindByIdAsync(started.Id, CancellationToken.None);

            Assert.Equal(["two", "three"], reloaded!.Messages.Select(message => message.Body));
        }
    }

    [Fact]
    public async Task PendingDeliveries_AreStoredQueriedAndDeleted()
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var deliveryId = Guid.NewGuid();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            repository.StorePending(new PendingDelivery
            {
                Id = deliveryId,
                RecipientPhoneId = phoneId,
                MessageId = new MessageId(Guid.NewGuid()),
                From = UniqueNumber(),
                Body = "call me",
                SentAt = DateTimeOffset.UtcNow,
            });
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var pending = await repository.FindPendingForPhoneAsync(phoneId, CancellationToken.None);
            Assert.Equal("call me", Assert.Single(pending).Body);

            repository.DeletePending(deliveryId);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            Assert.Empty(await repository.FindPendingForPhoneAsync(phoneId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ThreadAndPendingDelivery_WrittenInOneScope_CommitTogether()
    {
        // The reason PhoneSession still exists after the SIM merge: a delivery appends to a thread
        // stream and deletes the queued document, and a message both delivered and still waiting —
        // or dropped without ever arriving — is a state this module must never reach.
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        var participant = UniqueNumber();
        var deliveryId = Guid.NewGuid();
        var started = MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), phoneId, UniqueNumber(), [participant], MaxGroupParticipants);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            repository.StorePending(new PendingDelivery
            {
                Id = deliveryId,
                RecipientPhoneId = phoneId,
                MessageId = new MessageId(Guid.NewGuid()),
                From = participant,
                Body = "call me",
                SentAt = DateTimeOffset.UtcNow,
            });
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var thread = MessageThread.Create(started);
            repository.StartStream(thread, started);
            repository.Append(thread.Id, thread.RecordInbound(
                new MessageId(Guid.NewGuid()), participant, "call me", DateTimeOffset.UtcNow, RetentionLimit));
            repository.DeletePending(deliveryId);

            // One SaveChangesAsync covers the stream append and the document delete alike.
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();

            Assert.Single(Assert.Single(await repository.FindByPhoneAsync(phoneId, CancellationToken.None)).Messages);
            Assert.Empty(await repository.FindPendingForPhoneAsync(phoneId, CancellationToken.None));
        }
    }

    private async Task<PhoneDeviceId> ProvisionPhone(PhoneNumber number, CharacterId owner, string pin = "1234")
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
        var provisioned = new PhoneDeviceProvisioned(phoneId, number, pin, owner);
        repository.StartStream(PhoneDevice.Create(provisioned), provisioned);
        await repository.SaveChangesAsync(CancellationToken.None);
        return phoneId;
    }
}
