using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
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
public sealed class MartenPhoneDeviceRepository(IPhoneSession phoneSession) : IPhoneDeviceRepository
{
    private readonly IDocumentSession _session = phoneSession.Session;

    public async ValueTask<PhoneDevice?> FindByIdAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.LoadAsync<PhoneDevice>(phoneId, cancellationToken);

    /// <summary>
    /// Routing lookup for every send. Queries the canonical string form via
    /// <see cref="PhoneDevice.NumberValue"/> rather than the PhoneNumber struct: the duplicated
    /// column is what a unique index and an equality predicate can both actually use.
    /// </summary>
    public async ValueTask<PhoneDevice?> FindByNumberAsync(PhoneNumber number, CancellationToken cancellationToken)
    {
        var value = number.Value;
        return await _session.Query<PhoneDevice>().FirstOrDefaultAsync(phone => phone.NumberValue == value, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PhoneDevice>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<PhoneDevice>()
            .Where(phone => phone.RegisteredTo.Value == characterId.Value)
            .ToListAsync(cancellationToken);

    public void StartStream(PhoneDevice phone, PhoneDeviceProvisioned domainEvent)
        => _session.Events.StartStream<PhoneDevice>(phone.Id.Value, domainEvent);

    public void Append(PhoneDeviceId phoneId, object domainEvent)
        => _session.Events.Append(phoneId.Value, domainEvent);

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
            throw new PhoneNumberTakenException("That phone number is already issued to another phone.");
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

    public async ValueTask<ContactBook?> FindByPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.Query<ContactBook>().FirstOrDefaultAsync(book => book.PhoneId.Value == phoneId.Value, cancellationToken);

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

    public async ValueTask<MessageThread?> FindByKeyAsync(PhoneDeviceId phoneId, string threadKey, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .FirstOrDefaultAsync(thread => thread.OwnerPhoneId.Value == phoneId.Value && thread.ThreadKey == threadKey, cancellationToken);

    public async ValueTask<IReadOnlyList<MessageThread>> FindByPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .Where(thread => thread.OwnerPhoneId.Value == phoneId.Value)
            .OrderByDescending(thread => thread.LastMessageAt)
            .ToListAsync(cancellationToken);

    public void StartStream(MessageThread thread, MessageThreadStarted domainEvent)
        => _session.Events.StartStream<MessageThread>(thread.Id.Value, domainEvent);

    public void Append(MessageThreadId threadId, object domainEvent)
        => _session.Events.Append(threadId.Value, domainEvent);

    public async ValueTask<IReadOnlyList<PendingDelivery>> FindPendingForPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.Query<PendingDelivery>()
            .Where(delivery => delivery.RecipientPhoneId.Value == phoneId.Value)
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
public sealed class MartenPhoneSendWindowRepository : IPhoneSendWindowRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenPhoneSendWindowRepository(IPhoneStore store) => _session = store.LightweightSession();

    public async ValueTask<PhoneSendWindow?> FindAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.LoadAsync<PhoneSendWindow>(phoneId, cancellationToken);

    public async ValueTask StoreAsync(PhoneSendWindow window, CancellationToken cancellationToken)
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

    public async ValueTask<IReadOnlyList<PhoneDevice>> SearchPhonesAsync(string? numberFragment, CancellationToken cancellationToken)
    {
        var query = _session.Query<PhoneDevice>();

        return string.IsNullOrWhiteSpace(numberFragment)
            ? await query.Take(200).ToListAsync(cancellationToken)
            : await query.Where(phone => phone.NumberValue.Contains(numberFragment)).Take(200).ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MessageThread>> ListThreadsForPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken)
        => await _session.Query<MessageThread>()
            .Where(thread => thread.OwnerPhoneId.Value == phoneId.Value)
            .OrderByDescending(thread => thread.LastMessageAt)
            .ToListAsync(cancellationToken);
}
