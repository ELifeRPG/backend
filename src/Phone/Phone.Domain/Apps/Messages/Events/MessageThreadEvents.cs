using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Messages.Events;

public sealed record MessageThreadStarted(
    MessageThreadId Id,
    SimCardId OwnerSimCardId,
    IReadOnlyList<PhoneNumber> Participants,
    string ThreadKey);

/// <summary>
/// <paramref name="RetentionLimit"/> rides on the event rather than being read from the current
/// model at replay time: the cap that applied is a fact about the moment of the append. Without it,
/// replaying a stream after the SIM moved to a different handset would rebuild a different history.
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
