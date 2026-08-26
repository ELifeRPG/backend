using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Sims.Events;

namespace ELifeRPG.Phone.Application.Common;

public interface IPhoneModelRepository
{
    ValueTask<PhoneModel?> FindByIdAsync(PhoneModelId modelId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PhoneModel>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(PhoneModel model, PhoneModelCreated domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IPhoneDeviceRepository
{
    ValueTask<PhoneDevice?> FindByIdAsync(PhoneDeviceId deviceId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PhoneDevice>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken);

    void StartStream(PhoneDevice device, PhoneDeviceProvisioned domainEvent);

    void Append(PhoneDeviceId deviceId, object domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISimCardRepository
{
    ValueTask<SimCard?> FindByIdAsync(SimCardId simCardId, CancellationToken cancellationToken);

    /// <summary>
    /// The send path's routing step. Backed by a unique index on the canonical number, so an unknown
    /// number costs one indexed lookup rather than a scan.
    /// </summary>
    ValueTask<SimCard?> FindByNumberAsync(PhoneNumber number, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SimCard>> FindByCharacterAsync(CharacterId characterId, CancellationToken cancellationToken);

    /// <summary>
    /// The device already knows which SIMs it holds, so the installed set is loaded by id rather
    /// than queried on a nullable strongly-typed column.
    /// </summary>
    ValueTask<IReadOnlyList<SimCard>> FindByIdsAsync(IReadOnlyList<SimCardId> simCardIds, CancellationToken cancellationToken);

    void StartStream(SimCard simCard, SimCardIssued domainEvent);

    void Append(SimCardId simCardId, object domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IContactBookRepository
{
    ValueTask<ContactBook?> FindBySimAsync(SimCardId simCardId, CancellationToken cancellationToken);

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

    ValueTask<MessageThread?> FindByKeyAsync(SimCardId simCardId, string threadKey, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MessageThread>> FindBySimAsync(SimCardId simCardId, CancellationToken cancellationToken);

    void StartStream(MessageThread thread, MessageThreadStarted domainEvent);

    void Append(MessageThreadId threadId, object domainEvent);

    ValueTask<IReadOnlyList<PendingDelivery>> FindPendingForSimAsync(SimCardId simCardId, CancellationToken cancellationToken);

    void StorePending(PendingDelivery delivery);

    void DeletePending(Guid deliveryId);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deliberately separate from <see cref="IMessageThreadRepository"/>: the throttle counter does not
/// need to commit atomically with delivery. If the count lands and the send then fails, the caller
/// has spent one message's worth of quota — which fails closed, the safe direction for a throttle.
/// </summary>
public interface ISimSendWindowRepository
{
    ValueTask<SimSendWindow?> FindAsync(SimCardId simCardId, CancellationToken cancellationToken);

    ValueTask StoreAsync(SimSendWindow window, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only sweeps for the staff moderation surface. Separate from the player-facing repositories
/// because these deliberately ignore ownership and the guard chain — that is the point of a
/// moderation view — and nothing in the app paths should be able to reach them by accident.
/// </summary>
public interface IPhoneModerationRepository
{
    ValueTask<IReadOnlyList<SimCard>> SearchSimCardsAsync(string? numberFragment, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PhoneDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MessageThread>> ListThreadsForSimAsync(SimCardId simCardId, CancellationToken cancellationToken);
}
