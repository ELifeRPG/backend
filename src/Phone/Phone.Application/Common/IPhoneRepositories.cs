using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices.Events;

namespace ELifeRPG.Phone.Application.Common;

/// <summary>
/// One repository for the whole device, because there is one aggregate: the number, the PIN, the
/// blocklist, power and apps all live on it.
/// </summary>
public interface IPhoneDeviceRepository
{
    ValueTask<PhoneDevice?> FindByIdAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);

    /// <summary>
    /// The send path's routing step. Backed by a unique index on the canonical number, so an unknown
    /// number costs one indexed lookup rather than a scan.
    /// </summary>
    ValueTask<PhoneDevice?> FindByNumberAsync(PhoneNumber number, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PhoneDevice>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken);

    void StartStream(PhoneDevice phone, PhoneDeviceProvisioned domainEvent);

    void Append(PhoneDeviceId phoneId, object domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IContactBookRepository
{
    ValueTask<ContactBook?> FindByPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);

    void StartStream(ContactBook book, ContactBookOpened domainEvent);

    void Append(ContactBookId bookId, object domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns thread streams *and* pending deliveries on one session on purpose: a send fans out across
/// the sender's thread and every recipient's thread, and a half-delivered message — present in the
/// sender's history, missing from the recipient's — is the one outcome the send flow must never
/// produce. One session, one SaveChangesAsync, one commit.
/// </summary>
public interface IMessageThreadRepository
{
    ValueTask<MessageThread?> FindByIdAsync(MessageThreadId threadId, CancellationToken cancellationToken);

    ValueTask<MessageThread?> FindByKeyAsync(PhoneDeviceId phoneId, string threadKey, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MessageThread>> FindByPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);

    void StartStream(MessageThread thread, MessageThreadStarted domainEvent);

    void Append(MessageThreadId threadId, object domainEvent);

    ValueTask<IReadOnlyList<PendingDelivery>> FindPendingForPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);

    void StorePending(PendingDelivery delivery);

    void DeletePending(Guid deliveryId);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deliberately separate from <see cref="IMessageThreadRepository"/>: the throttle counter does not
/// need to commit atomically with delivery. If the count lands and the send then fails, the caller
/// has spent one message's worth of quota — which fails closed, the safe direction for a throttle.
/// </summary>
public interface IPhoneSendWindowRepository
{
    ValueTask<PhoneSendWindow?> FindAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);

    ValueTask StoreAsync(PhoneSendWindow window, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only sweeps for the staff moderation surface. Separate from the player-facing repositories
/// because these deliberately ignore ownership and the guard chain — that is the point of a
/// moderation view — and nothing in the app paths should be able to reach them by accident.
/// </summary>
public interface IPhoneModerationRepository
{
    ValueTask<IReadOnlyList<PhoneDevice>> SearchPhonesAsync(string? numberFragment, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MessageThread>> ListThreadsForPhoneAsync(PhoneDeviceId phoneId, CancellationToken cancellationToken);
}
