using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Phone.Domain.Sims.Events;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.Phone.Domain.Exceptions;
using Marten;
using Npgsql;

namespace ELifeRPG.Phone.Infrastructure.Common;

/// <summary>
/// Every repository here holds one session for its own lifetime — a secondary Marten store gets no
/// DI-injected scoped session, the same reason MartenItemRepository and MartenCharacterRepository
/// own theirs. Sessions are untenanted: the Phone module is hive-wide, so a number reaches its owner
/// regardless of which gameserver they are on.
///
/// Note the standing id gotcha documented on MartenAccountRepository: LoadAsync takes the
/// strongly-typed id, while Events.StartStream takes the raw Guid. Mixing them up throws
/// DocumentIdTypeMismatchException at runtime, not compile time.
/// </summary>
public sealed class MartenPhoneModelRepository(IPhoneSession phoneSession) : IPhoneModelRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<PhoneModel?> FindByIdAsync(PhoneModelId modelId, CancellationToken cancellationToken)
        => await _session.LoadAsync<PhoneModel>(modelId, cancellationToken);

    public async ValueTask<IReadOnlyList<PhoneModel>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<PhoneModel>().ToListAsync(cancellationToken);

    public void StartStream(PhoneModel model, PhoneModelCreated domainEvent)
        => _session.Events.StartStream<PhoneModel>(model.Id.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

}

public sealed class MartenPhoneDeviceRepository(IPhoneSession phoneSession) : IPhoneDeviceRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<PhoneDevice?> FindByIdAsync(PhoneDeviceId deviceId, CancellationToken cancellationToken)
        => await _session.LoadAsync<PhoneDevice>(deviceId, cancellationToken);

    public async ValueTask<IReadOnlyList<PhoneDevice>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<PhoneDevice>().Where(device => device.BoundCharacterId.Value == characterId.Value).ToListAsync(cancellationToken);

    public void StartStream(PhoneDevice device, PhoneDeviceProvisioned domainEvent)
        => _session.Events.StartStream<PhoneDevice>(device.Id.Value, domainEvent);

    public void Append(PhoneDeviceId deviceId, object domainEvent)
        => _session.Events.Append(deviceId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

}

public sealed class MartenSimCardRepository(IPhoneSession phoneSession) : ISimCardRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<SimCard?> FindByIdAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.LoadAsync<SimCard>(simCardId, cancellationToken);

    /// <summary>
    /// Routing lookup for every send. Queries the canonical string form via
    /// <see cref="SimCard.NumberValue"/> rather than the PhoneNumber struct: the duplicated column
    /// is what a unique index and an equality predicate can both actually use.
    /// </summary>
    public async ValueTask<SimCard?> FindByNumberAsync(PhoneNumber number, CancellationToken cancellationToken)
    {
        var value = number.Value;
        return await _session.Query<SimCard>().FirstOrDefaultAsync(sim => sim.NumberValue == value, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<SimCard>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<SimCard>().Where(sim => sim.RegisteredTo.Value == characterId.Value).ToListAsync(cancellationToken);

    /// <summary>
    /// Loaded one at a time, deliberately. LoadManyAsync only has Guid/string/int/long overloads and
    /// SimCard's identity is a SimCardId, so the unwrapped Guids throw DocumentIdTypeMismatchException
    /// (the gotcha MartenAccountRepository documents); and Marten's LINQ translates neither
    /// `ids.Contains(sim.Id)` nor `ids.Contains(sim.Id.Value)` — both raise BadLinqExpressionException.
    /// The set here is a device's installed SIMs, capped by PhoneModel.SimSlots at one or two, so the
    /// round trips are bounded and tiny.
    /// </summary>
    public async ValueTask<IReadOnlyList<SimCard>> FindByIdsAsync(IReadOnlyList<SimCardId> simCardIds, CancellationToken cancellationToken)
    {
        var found = new List<SimCard>(simCardIds.Count);

        foreach (var simCardId in simCardIds)
        {
            if (await _session.LoadAsync<SimCard>(simCardId, cancellationToken) is { } sim)
            {
                found.Add(sim);
            }
        }

        return found;
    }

    public void StartStream(SimCard simCard, SimCardIssued domainEvent)
        => _session.Events.StartStream<SimCard>(simCard.Id.Value, domainEvent);

    public void Append(SimCardId simCardId, object domainEvent)
        => _session.Events.Append(simCardId.Value, domainEvent);

    /// <summary>
    /// Translates the number unique-index violation into a domain exception so Phone.Application can
    /// retry with a fresh number without referencing Marten or Npgsql — the same move
    /// MartenShopListingRepository makes for a purchase conflict. Anything else propagates untouched.
    /// </summary>
    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsNumberCollision(exception))
        {
            throw new PhoneNumberTakenException("That phone number is already issued to another SIM card.");
        }
    }

    private static bool IsNumberCollision(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres =>
            postgres.ConstraintName?.Contains("number_value", StringComparison.OrdinalIgnoreCase) == true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsNumberCollision),
        { InnerException: { } inner } => IsNumberCollision(inner),
        _ => false,
    };
}

public sealed class MartenContactBookRepository(IPhoneSession phoneSession) : IContactBookRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<ContactBook?> FindBySimAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.Query<ContactBook>().FirstOrDefaultAsync(book => book.SimCardId.Value == simCardId.Value, cancellationToken);

    public void StartStream(ContactBook book, ContactBookOpened domainEvent)
        => _session.Events.StartStream<ContactBook>(book.Id.Value, domainEvent);

    public void Append(ContactBookId bookId, object domainEvent)
        => _session.Events.Append(bookId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

}

public sealed class MartenMessageThreadRepository(IPhoneSession phoneSession) : IMessageThreadRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<MessageThread?> FindByIdAsync(MessageThreadId threadId, CancellationToken cancellationToken)
        => await _session.LoadAsync<MessageThread>(threadId, cancellationToken);

    public async ValueTask<MessageThread?> FindByKeyAsync(SimCardId simCardId, string threadKey, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .FirstOrDefaultAsync(thread => thread.OwnerSimCardId.Value == simCardId.Value && thread.ThreadKey == threadKey, cancellationToken);

    public async ValueTask<IReadOnlyList<MessageThread>> FindBySimAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .Where(thread => thread.OwnerSimCardId.Value == simCardId.Value)
            .OrderByDescending(thread => thread.LastMessageAt)
            .ToListAsync(cancellationToken);

    public void StartStream(MessageThread thread, MessageThreadStarted domainEvent)
        => _session.Events.StartStream<MessageThread>(thread.Id.Value, domainEvent);

    public void Append(MessageThreadId threadId, object domainEvent)
        => _session.Events.Append(threadId.Value, domainEvent);

    public async ValueTask<IReadOnlyList<PendingDelivery>> FindPendingForSimAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.Query<PendingDelivery>()
            .Where(delivery => delivery.RecipientSimCardId.Value == simCardId.Value)
            .OrderBy(delivery => delivery.SentAt)
            .ToListAsync(cancellationToken);

    public void StorePending(PendingDelivery delivery) => _session.Store(delivery);

    public void DeletePending(Guid deliveryId) => _session.Delete<PendingDelivery>(deliveryId);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// The one repository that keeps its own session rather than joining <see cref="IPhoneSession"/>.
/// The throttle counter must commit on its own: sharing the unit of work would make StoreAsync
/// flush whatever else the scope had pending, and the counter deliberately does not need to be
/// atomic with delivery — spending quota on a send that then fails errs closed, the safe direction.
/// </summary>
public sealed class MartenSimSendWindowRepository : ISimSendWindowRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenSimSendWindowRepository(IPhoneStore store) => _session = store.LightweightSession();

    public async ValueTask<SimSendWindow?> FindAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.LoadAsync<SimSendWindow>(simCardId, cancellationToken);

    public async ValueTask StoreAsync(SimSendWindow window, CancellationToken cancellationToken)
    {
        _session.Store(window);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}

/// <summary>
/// Staff-only reads. Ownership is deliberately not consulted: a moderator looking into a number they
/// do not own is the whole purpose. Scope enforcement lives at the endpoint (phone:manage).
/// </summary>
public sealed class MartenPhoneModerationRepository(IPhoneSession phoneSession) : IPhoneModerationRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<IReadOnlyList<SimCard>> SearchSimCardsAsync(string? numberFragment, CancellationToken cancellationToken)
    {
        var query = _session.Query<SimCard>();

        return string.IsNullOrWhiteSpace(numberFragment)
            ? await query.Take(200).ToListAsync(cancellationToken)
            : await query.Where(sim => sim.NumberValue.Contains(numberFragment)).Take(200).ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PhoneDevice>> ListDevicesAsync(CancellationToken cancellationToken)
        => await _session.Query<PhoneDevice>().Take(200).ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<MessageThread>> ListThreadsForSimAsync(SimCardId simCardId, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .Where(thread => thread.OwnerSimCardId.Value == simCardId.Value)
            .OrderByDescending(thread => thread.LastMessageAt)
            .ToListAsync(cancellationToken);
}
