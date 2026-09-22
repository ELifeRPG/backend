using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

/// <summary>
/// The Messages app's state: one stream per (phone, participant set). That single key is what gives
/// per-number history and ad-hoc SMS-style group threads at the same time — there is no group object
/// to create, name or administer, exactly like real SMS.
///
/// Threads store bare numbers. Resolving display names is the Contacts app's job, on the client.
/// </summary>
public class MessageThread
{
    public MessageThreadId Id { get; private set; }

    public PhoneDeviceId OwnerPhoneId { get; private set; }

    /// <summary>Sorted and deduplicated, and never includes the owner's own number.</summary>
    public List<PhoneNumber> Participants { get; private set; } = [];

    /// <summary>
    /// Canonical rendering of <see cref="Participants"/>, carried as a plain string because Marten
    /// can neither index nor translate a predicate against a <see cref="PhoneNumber"/> — so the send
    /// path can look a thread up in one hit. Backed by a plain index on ThreadKey alone; the lookup
    /// filters (OwnerPhoneId, ThreadKey) but nothing enforces that pair.
    /// </summary>
    public string ThreadKey { get; private set; } = string.Empty;

    public List<Message> Messages { get; private set; } = [];

    public int UnreadCount { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>
    /// The sequence the next appended message will be given. Starts at 1, not 0, so that
    /// <see cref="RetrievedThrough"/> at its own default of 0 unambiguously means "nothing retrieved
    /// yet" rather than colliding with a real message's number.
    ///
    /// The initializer is load-bearing for the same reason every <c>HiveSettings</c> knob carries
    /// one: a <see cref="MessageThread"/> document written before this field existed deserializes
    /// with it absent, and System.Text.Json leaves an absent property at its initialized value. Without
    /// this initializer a pre-existing thread would mint sequence 0 for its next message and collide
    /// with its own (unnumbered) history instead of continuing cleanly from where the poll already
    /// treats everything as invisible — see the rollout note in README.md.
    /// </summary>
    public int NextSequence { get; private set; } = 1;

    /// <summary>
    /// The highest sequence a poller has told this thread it retrieved — see
    /// <see cref="MarkRetrievedThrough"/>. Deliberately independent of <see cref="UnreadCount"/>:
    /// retrieved means the mod pulled the content down, read means the player opened the thread, and
    /// conflating them would mean a background poll could silently clear a player's unread badge.
    /// </summary>
    public int RetrievedThrough { get; private set; }

    /// <summary>
    /// Order- and formatting-independent, so two sends naming the same people in different orders
    /// land in one thread instead of two.
    /// </summary>
    public static string BuildThreadKey(IEnumerable<PhoneNumber> participants) =>
        string.Join('|', Normalise(participants).Select(number => number.Value));

    public static MessageThreadStarted Start(
        MessageThreadId id,
        PhoneDeviceId ownerPhoneId,
        PhoneNumber ownerNumber,
        IReadOnlyList<PhoneNumber> participants,
        int maxGroupParticipants)
    {
        // Addressing a group that includes yourself is normal, so the owner is dropped rather than
        // rejected — the thread is always "the others".
        var others = Normalise(participants.Where(number => number != ownerNumber));

        if (others.Count == 0)
        {
            throw new ArgumentException("A thread needs at least one participant besides the owner.", nameof(participants));
        }

        if (others.Count > maxGroupParticipants)
        {
            throw new ArgumentOutOfRangeException(
                nameof(participants),
                others.Count,
                $"A thread allows at most {maxGroupParticipants} group participants.");
        }

        return new MessageThreadStarted(id, ownerPhoneId, others, BuildThreadKey(others));
    }

    public static MessageThread Create(MessageThreadStarted domainEvent)
    {
        var thread = new MessageThread();
        thread.Apply(domainEvent);
        return thread;
    }

    public OutboundMessageRecorded RecordOutbound(MessageId messageId, PhoneNumber from, string body, DateTimeOffset sentAt, int retentionLimit)
    {
        var domainEvent = new OutboundMessageRecorded(Id, messageId, from, EnsureBody(body), sentAt, retentionLimit, NextSequence);
        Apply(domainEvent);
        return domainEvent;
    }

    public InboundMessageRecorded RecordInbound(MessageId messageId, PhoneNumber from, string body, DateTimeOffset sentAt, int retentionLimit)
    {
        var domainEvent = new InboundMessageRecorded(Id, messageId, from, EnsureBody(body), sentAt, retentionLimit, NextSequence);
        Apply(domainEvent);
        return domainEvent;
    }

    public ThreadMarkedRead MarkRead()
    {
        var domainEvent = new ThreadMarkedRead(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Records that a poller has pulled everything through <paramref name="sequence"/>. Clamped to
    /// the highest sequence this thread has actually issued, so a caller acking a stale or
    /// over-eager value (an off-by-one, a wraparound, a client bug) cannot advance the watermark past
    /// what could possibly have been delivered and go permanently blind to everything after.
    /// </summary>
    public ThreadRetrievedThrough MarkRetrievedThrough(int sequence)
    {
        var effective = Math.Min(sequence, NextSequence - 1);
        var domainEvent = new ThreadRetrievedThrough(Id, effective);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(MessageThreadStarted domainEvent)
    {
        Id = domainEvent.Id;
        OwnerPhoneId = domainEvent.OwnerPhoneId;
        Participants = [.. domainEvent.Participants];
        ThreadKey = domainEvent.ThreadKey;
    }

    public void Apply(OutboundMessageRecorded domainEvent) =>
        Append(
            new Message(domainEvent.MessageId, domainEvent.From, domainEvent.Body, domainEvent.SentAt, IsOutbound: true, domainEvent.Sequence),
            domainEvent.RetentionLimit);

    public void Apply(InboundMessageRecorded domainEvent)
    {
        Append(
            new Message(domainEvent.MessageId, domainEvent.From, domainEvent.Body, domainEvent.SentAt, IsOutbound: false, domainEvent.Sequence),
            domainEvent.RetentionLimit);
        UnreadCount++;
    }

    public void Apply(ThreadMarkedRead domainEvent) => UnreadCount = 0;

    /// <summary>
    /// Max rather than assignment: a duplicate or out-of-order replay of this event (a retried ack
    /// that landed twice, or events applied out of commit order) must not rewind the watermark.
    /// </summary>
    public void Apply(ThreadRetrievedThrough domainEvent) => RetrievedThrough = Math.Max(RetrievedThrough, domainEvent.Sequence);

    private void Append(Message message, int retentionLimit)
    {
        Messages.Add(message);
        LastMessageAt = message.SentAt;

        // Before trimming, and as a max rather than an assignment: trimming the front of Messages
        // must never rewind the counter, which is the entire reason this is a counter and not the
        // list's own length.
        NextSequence = Math.Max(NextSequence, message.Sequence + 1);

        // Trimming against the limit carried on the event, not against the current setting: that is
        // what keeps a replay rebuilding the history that actually existed.
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
