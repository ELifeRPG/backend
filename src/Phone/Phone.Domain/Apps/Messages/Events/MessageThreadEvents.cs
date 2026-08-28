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
/// </summary>
public sealed record OutboundMessageRecorded(
    MessageThreadId Id,
    MessageId MessageId,
    PhoneNumber From,
    string Body,
    DateTimeOffset SentAt,
    int RetentionLimit);

/// <inheritdoc cref="OutboundMessageRecorded"/>
public sealed record InboundMessageRecorded(
    MessageThreadId Id,
    MessageId MessageId,
    PhoneNumber From,
    string Body,
    DateTimeOffset SentAt,
    int RetentionLimit);

public sealed record ThreadMarkedRead(MessageThreadId Id);
