using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

/// <summary>
/// The Messages app's state: one stream per (SIM, participant set). That single key is what gives
/// per-SIM history and ad-hoc SMS-style group threads at the same time — there is no group object to
/// create, name or administer, exactly like real SMS.
///
/// Threads store bare numbers. Resolving display names is the Contacts app's job, on the client.
/// </summary>
public class MessageThread
{
    [JsonInclude]
    public MessageThreadId Id { get; private set; }

    [JsonInclude]
    public SimCardId OwnerSimCardId { get; private set; }

    /// <summary>Sorted and deduplicated, and never includes the owner's own number.</summary>
    [JsonInclude]
    public List<PhoneNumber> Participants { get; private set; } = [];

    /// <summary>
    /// Canonical rendering of <see cref="Participants"/>, carried as a plain string so Marten can put
    /// a unique index on (OwnerSimCardId, ThreadKey) and the send path can look a thread up in one hit.
    /// </summary>
    [JsonInclude]
    public string ThreadKey { get; private set; } = string.Empty;

    [JsonInclude]
    public List<Message> Messages { get; private set; } = [];

    [JsonInclude]
    public int UnreadCount { get; private set; }

    [JsonInclude]
    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>
    /// Order- and formatting-independent, so two sends naming the same people in different orders
    /// land in one thread instead of two.
    /// </summary>
    public static string BuildThreadKey(IEnumerable<PhoneNumber> participants) =>
        string.Join('|', Normalise(participants).Select(number => number.Value));

    public static MessageThreadStarted Start(
        MessageThreadId id,
        SimCardId ownerSimCardId,
        PhoneNumber ownerNumber,
        IReadOnlyList<PhoneNumber> participants,
        PhoneModel model)
    {
        // Addressing a group that includes yourself is normal, so the owner is dropped rather than
        // rejected — the thread is always "the others".
        var others = Normalise(participants.Where(number => number != ownerNumber));

        if (others.Count == 0)
        {
            throw new ArgumentException("A thread needs at least one participant besides the owner.", nameof(participants));
        }

        if (others.Count > model.MaxGroupParticipants)
        {
            throw new ArgumentOutOfRangeException(
                nameof(participants),
                others.Count,
                $"Model {model.DisplayName} allows at most {model.MaxGroupParticipants} group participants.");
        }

        return new MessageThreadStarted(id, ownerSimCardId, others, BuildThreadKey(others));
    }

    public static MessageThread Create(MessageThreadStarted domainEvent)
    {
        var thread = new MessageThread();
        thread.Apply(domainEvent);
        return thread;
    }

    public OutboundMessageRecorded RecordOutbound(MessageId messageId, PhoneNumber from, string body, DateTimeOffset sentAt, PhoneModel model)
    {
        var domainEvent = new OutboundMessageRecorded(Id, messageId, from, EnsureBody(body), sentAt, model.ThreadMessageLimit);
        Apply(domainEvent);
        return domainEvent;
    }

    public InboundMessageRecorded RecordInbound(MessageId messageId, PhoneNumber from, string body, DateTimeOffset sentAt, PhoneModel model)
    {
        var domainEvent = new InboundMessageRecorded(Id, messageId, from, EnsureBody(body), sentAt, model.ThreadMessageLimit);
        Apply(domainEvent);
        return domainEvent;
    }

    public ThreadMarkedRead MarkRead()
    {
        var domainEvent = new ThreadMarkedRead(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(MessageThreadStarted domainEvent)
    {
        Id = domainEvent.Id;
        OwnerSimCardId = domainEvent.OwnerSimCardId;
        Participants = [.. domainEvent.Participants];
        ThreadKey = domainEvent.ThreadKey;
    }

    public void Apply(OutboundMessageRecorded domainEvent) =>
        Append(new Message(domainEvent.MessageId, domainEvent.From, domainEvent.Body, domainEvent.SentAt, IsOutbound: true), domainEvent.RetentionLimit);

    public void Apply(InboundMessageRecorded domainEvent)
    {
        Append(new Message(domainEvent.MessageId, domainEvent.From, domainEvent.Body, domainEvent.SentAt, IsOutbound: false), domainEvent.RetentionLimit);
        UnreadCount++;
    }

    public void Apply(ThreadMarkedRead domainEvent) => UnreadCount = 0;

    private void Append(Message message, int retentionLimit)
    {
        Messages.Add(message);
        LastMessageAt = message.SentAt;

        // Trimming against the limit carried on the event, not against the current model: that is
        // what makes moving a SIM into a smaller handset cost you the backlog on the next message.
        if (retentionLimit > 0 && Messages.Count > retentionLimit)
        {
            Messages.RemoveRange(0, Messages.Count - retentionLimit);
        }
    }

    private static List<PhoneNumber> Normalise(IEnumerable<PhoneNumber> participants) =>
        [.. participants.Distinct().OrderBy(number => number.Value, StringComparer.Ordinal)];

    private static string EnsureBody(string body)
    {
        // Length is capped by HiveSettings in the send handler, not here — it is a deployment knob,
        // not a domain invariant.
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Message body is required.", nameof(body));
        }

        return body;
    }
}
