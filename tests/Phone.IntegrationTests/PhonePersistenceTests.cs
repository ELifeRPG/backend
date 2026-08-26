using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Phone.Domain.Sims.Events;
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

    private static PhoneModel BuildModel(int simSlots = 2, int contactLimit = 50, int threadMessageLimit = 30) =>
        PhoneModel.Create(PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()), "Test handset", 1, null, simSlots,
            [AppKey.Messages, AppKey.Contacts], contactLimit, threadMessageLimit, 5));

    [Fact]
    public async Task SimCard_RoundTripsIncludingTheNumberValueObject()
    {
        var number = UniqueNumber();
        var owner = new CharacterId(Guid.NewGuid());
        var simId = new SimCardId(Guid.NewGuid());

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
            var issued = new SimCardIssued(simId, number, owner);
            repository.StartStream(SimCard.Create(issued), issued);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
            var reloaded = await repository.FindByIdAsync(simId, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal(number, reloaded.Number);
            Assert.Equal(number.Value, reloaded.NumberValue);
            Assert.Equal(SimCardStatus.Active, reloaded.Status);
        }
    }

    [Fact]
    public async Task SimCard_IsFoundByItsNumber()
    {
        var number = UniqueNumber();
        var simId = await IssueSim(number, new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();

        // Parsed from a differently formatted spelling on purpose: routing must not care.
        var found = await repository.FindByNumberAsync(PhoneNumber.Parse($"+{number.Value}"), CancellationToken.None);

        Assert.Equal(simId, found?.Id);
    }

    [Fact]
    public async Task SimCard_WithADuplicateNumber_IsRejectedByTheUniqueIndex()
    {
        var number = UniqueNumber();
        await IssueSim(number, new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
        var issued = new SimCardIssued(new SimCardId(Guid.NewGuid()), number, new CharacterId(Guid.NewGuid()));
        repository.StartStream(SimCard.Create(issued), issued);

        // The repository translates the unique-index violation into a domain exception so
        // Phone.Application can retry a fresh number without referencing Marten or Npgsql.
        await Assert.ThrowsAsync<PhoneNumberTakenException>(
            async () => await repository.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SimCard_AppendedEventsAreProjectedOntoTheDocument()
    {
        var blocked = UniqueNumber();
        var simId = await IssueSim(UniqueNumber(), new CharacterId(Guid.NewGuid()));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
            var sim = await repository.FindByIdAsync(simId, CancellationToken.None);
            repository.Append(simId, sim!.Block(blocked));
            repository.Append(simId, sim.Suspend("Police order"));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
            var reloaded = await repository.FindByIdAsync(simId, CancellationToken.None);

            Assert.Equal(SimCardStatus.Suspended, reloaded!.Status);
            Assert.True(reloaded.IsBlocked(blocked));
        }
    }

    [Fact]
    public async Task SimCards_AreFoundByRegisteredCharacter()
    {
        var owner = new CharacterId(Guid.NewGuid());
        await IssueSim(UniqueNumber(), owner);
        await IssueSim(UniqueNumber(), owner);
        await IssueSim(UniqueNumber(), new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();

        Assert.Equal(2, (await repository.FindByCharacterAsync(owner, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SimCards_AreLoadedByIdSet()
    {
        var first = await IssueSim(UniqueNumber(), new CharacterId(Guid.NewGuid()));
        var second = await IssueSim(UniqueNumber(), new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();

        var loaded = await repository.FindByIdsAsync([first, second], CancellationToken.None);

        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task PhoneDevices_AreFoundByBoundCharacter()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var modelId = new PhoneModelId(Guid.NewGuid());
        await ProvisionDevice(modelId, owner);
        await ProvisionDevice(modelId, owner);
        await ProvisionDevice(modelId, new CharacterId(Guid.NewGuid()));

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();

        Assert.Equal(2, (await repository.FindByCharacterAsync(owner, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task PhoneModel_RoundTripsItsCapabilityNumbers()
    {
        var model = BuildModel(simSlots: 2, contactLimit: 250, threadMessageLimit: 500);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPhoneModelRepository>();
            var created = PhoneModel.Define(model.Id, "Smartphone", 3, null, 2, [AppKey.Messages, AppKey.Contacts], 250, 500, 8);
            repository.StartStream(PhoneModel.Create(created), created);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPhoneModelRepository>();
            var reloaded = await repository.FindByIdAsync(model.Id, CancellationToken.None);

            Assert.Equal(2, reloaded!.SimSlots);
            Assert.Equal(250, reloaded.ContactLimit);
            Assert.Equal(500, reloaded.ThreadMessageLimit);
            Assert.True(reloaded.Supports(AppKey.Contacts));
        }
    }

    [Fact]
    public async Task ContactBook_IsFoundBySimAndKeepsItsContacts()
    {
        var simId = new SimCardId(Guid.NewGuid());
        var bookId = new ContactBookId(Guid.NewGuid());
        var saved = UniqueNumber();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IContactBookRepository>();
            var opened = new ContactBookOpened(bookId, simId);
            var book = ContactBook.Create(opened);
            repository.StartStream(book, opened);
            repository.Append(bookId, book.SaveContact(new ContactId(Guid.NewGuid()), saved, "Dispatcher", BuildModel()));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IContactBookRepository>();
            var reloaded = await repository.FindBySimAsync(simId, CancellationToken.None);

            Assert.Equal(bookId, reloaded!.Id);
            Assert.Equal("Dispatcher", reloaded.Find(saved)?.DisplayName);
        }
    }

    [Fact]
    public async Task MessageThread_IsFoundByItsSimAndThreadKey()
    {
        var simId = new SimCardId(Guid.NewGuid());
        var participant = UniqueNumber();
        var owner = UniqueNumber();
        var model = BuildModel();
        var started = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), simId, owner, [participant], model);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var thread = MessageThread.Create(started);
            repository.StartStream(thread, started);
            repository.Append(thread.Id, thread.RecordInbound(new MessageId(Guid.NewGuid()), participant, "where are you", DateTimeOffset.UtcNow, model));
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();

            var byKey = await repository.FindByKeyAsync(simId, started.ThreadKey, CancellationToken.None);
            var bySim = await repository.FindBySimAsync(simId, CancellationToken.None);

            Assert.Equal(started.Id, byKey?.Id);
            Assert.Equal("where are you", Assert.Single(byKey!.Messages).Body);
            Assert.Equal(1, byKey.UnreadCount);
            Assert.Single(bySim);
        }
    }

    [Fact]
    public async Task MessageThread_RetentionLimitIsEnforcedAcrossAReload()
    {
        // Proves the trim survives persistence, not just in-memory replay: the limit rides on each
        // event, so the projection applies it the same way the aggregate did.
        var simId = new SimCardId(Guid.NewGuid());
        var participant = UniqueNumber();
        var model = BuildModel(threadMessageLimit: 2);
        var started = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), simId, UniqueNumber(), [participant], model);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            var thread = MessageThread.Create(started);
            repository.StartStream(thread, started);
            foreach (var body in (string[])["one", "two", "three"])
            {
                repository.Append(thread.Id, thread.RecordInbound(new MessageId(Guid.NewGuid()), participant, body, DateTimeOffset.UtcNow, model));
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
        var simId = new SimCardId(Guid.NewGuid());
        var deliveryId = Guid.NewGuid();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            repository.StorePending(new PendingDelivery
            {
                Id = deliveryId,
                RecipientSimCardId = simId,
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
            var pending = await repository.FindPendingForSimAsync(simId, CancellationToken.None);
            Assert.Equal("call me", Assert.Single(pending).Body);

            repository.DeletePending(deliveryId);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageThreadRepository>();
            Assert.Empty(await repository.FindPendingForSimAsync(simId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task DeviceAndSim_WrittenInOneScope_CommitTogether()
    {
        // The reason PhoneSession exists: installing a SIM appends to two streams, and a device
        // claiming a SIM that is not installed is a state this module must never reach.
        var owner = new CharacterId(Guid.NewGuid());
        var model = BuildModel(simSlots: 1);
        var deviceId = await ProvisionDevice(model.Id, owner);
        var simId = await IssueSim(UniqueNumber(), owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
            var sims = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();

            var device = await devices.FindByIdAsync(deviceId, CancellationToken.None);
            var sim = await sims.FindByIdAsync(simId, CancellationToken.None);

            devices.Append(deviceId, device!.InstallSim(simId, model));
            sims.Append(simId, sim!.InstallInto(deviceId));

            // One SaveChangesAsync on either repository commits both — they share the session.
            await devices.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
            var sims = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();

            var device = await devices.FindByIdAsync(deviceId, CancellationToken.None);
            var sim = await sims.FindByIdAsync(simId, CancellationToken.None);

            Assert.Contains(simId, device!.InstalledSims);
            Assert.Equal(deviceId, sim!.InstalledIn);

            var installed = await sims.FindByIdsAsync(device.InstalledSims, CancellationToken.None);
            Assert.Equal(simId, Assert.Single(installed).Id);
        }
    }

    private async Task<SimCardId> IssueSim(PhoneNumber number, CharacterId owner)
    {
        var simId = new SimCardId(Guid.NewGuid());
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimCardRepository>();
        var issued = new SimCardIssued(simId, number, owner);
        repository.StartStream(SimCard.Create(issued), issued);
        await repository.SaveChangesAsync(CancellationToken.None);
        return simId;
    }

    private async Task<PhoneDeviceId> ProvisionDevice(PhoneModelId modelId, CharacterId owner)
    {
        var deviceId = new PhoneDeviceId(Guid.NewGuid());
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPhoneDeviceRepository>();
        var provisioned = new PhoneDeviceProvisioned(deviceId, modelId, owner);
        repository.StartStream(PhoneDevice.Create(provisioned), provisioned);
        await repository.SaveChangesAsync(CancellationToken.None);
        return deviceId;
    }
}
