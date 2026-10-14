using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Messages.Events;

public sealed record MessageThreadStarted(
    MessageThreadId Id,
    PhoneDeviceId OwnerPhoneId,
    IReadOnlyList<PhoneNumber> Participants,
    string ThreadKey);

/// <summary>
/// <paramref name="RetentionLimit"/> rides on the event rather than being read from the current
/// setting at replay time: the cap that applied is a fact about the moment of the append. It matters
/// more now than it did, not less — <c>HiveSettings.PhoneThreadMessageLimit</c> is editable at
/// runtime, so without this a staff member raising the cap would silently rewrite every history.
///
/// <paramref name="Sequence"/> rides on the event for the same reason: it is what the aggregate
/// assigned at the moment of the append (see <see cref="MessageThread.Append"/>), and carrying it
/// keeps a replay reproducing the identical numbers rather than re-deriving them from apply order.
/// </summary>
public sealed record OutboundMessageRecorded(
    MessageThreadId Id,
    MessageId MessageId,
    PhoneNumber From,
    string Body,
    DateTimeOffset SentAt,
    int RetentionLimit,
    int Sequence);

/// <inheritdoc cref="OutboundMessageRecorded"/>
public sealed record InboundMessageRecorded(
    MessageThreadId Id,
    MessageId MessageId,
    PhoneNumber From,
    string Body,
    DateTimeOffset SentAt,
    int RetentionLimit,
    int Sequence);

public sealed record ThreadMarkedRead(MessageThreadId Id);

/// <summary>
/// The mod's counterpart to <see cref="ThreadMarkedRead"/>, and deliberately not the same event:
/// this says "the mod pulled these messages down", not "the player opened the thread" — see
/// <see cref="MessageThread.RetrievedThrough"/> for why the two must stay independent.
/// </summary>
public sealed record ThreadRetrievedThrough(MessageThreadId Id, int Sequence);
