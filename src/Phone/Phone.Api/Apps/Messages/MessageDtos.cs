using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Api.Apps.Messages;

public sealed record MessageDto(Guid Id, string From, string Body, DateTimeOffset SentAt, bool IsOutbound, int Sequence)
{
    public static MessageDto Create(Message source) =>
        new(source.Id.Value, source.From.Value, source.Body, source.SentAt, source.IsOutbound, source.Sequence);
}

/// <summary>
/// The thread-list projection: participants, the unread count, a timestamp and
/// <paramref name="LastMessage"/>, the newest retained message, which an inbox renders as the
/// one-line preview. The rest of the history is left to <see cref="MessageThreadDto"/> — only the
/// opened thread needs it. <paramref name="LastMessage"/> comes from the aggregate the list already
/// loads, so it costs no extra query and cannot drift from <paramref name="LastMessageAt"/> or
/// <paramref name="UnreadCount"/> the way a separate detail call could.
///
/// <paramref name="LastMessage"/> is null only for a thread with no retained message, which a thread
/// started by a send never is.
/// </summary>
public sealed record MessageThreadSummaryDto(
    Guid Id,
    IReadOnlyList<string> Participants,
    int UnreadCount,
    DateTimeOffset LastMessageAt,
    MessageDto? LastMessage)
{
    // Messages is append-ordered and retention trims only its front, so the last entry is the newest.
    public static MessageThreadSummaryDto Create(MessageThread source) => new(
        source.Id.Value,
        [.. source.Participants.Select(number => number.Value)],
        source.UnreadCount,
        source.LastMessageAt,
        source.Messages.Count > 0 ? MessageDto.Create(source.Messages[^1]) : null);
}

public sealed record MessageThreadDto(
    Guid Id,
    IReadOnlyList<string> Participants,
    int UnreadCount,
    DateTimeOffset LastMessageAt,
    IReadOnlyList<MessageDto> Messages)
{
    public static MessageThreadDto Create(MessageThread source) => new(
        source.Id.Value,
        [.. source.Participants.Select(number => number.Value)],
        source.UnreadCount,
        source.LastMessageAt,
        [.. source.Messages.Select(MessageDto.Create)]);
}

public sealed record BlockNumberRequestDto(string Number);

public sealed record SendMessageRequestDto(IReadOnlyList<string> To, string Body);

/// <summary>
/// <paramref name="UndeliverableRecipients"/> reports only what a real network would reveal —
/// numbers that do not exist, or are suspended or retired. A blocked recipient is deliberately
/// absent: the sender must not be able to detect a block.
/// </summary>
public sealed record SendMessageResponseDto(Guid ThreadId, Guid MessageId, IReadOnlyList<string> UndeliverableRecipients);

/// <summary>
/// A thread as a poll reports it: the same metadata <see cref="MessageThreadSummaryDto"/> carries,
/// plus only those messages retrieved through <see cref="RetrievedThrough"/> have not yet been seen —
/// not the thread's whole history.
///
/// <paramref name="HighestSequence"/> is the highest sequence among <paramref name="Messages"/>: the
/// value to send back as <c>throughSequence</c> on the ack, handed over directly so a client never
/// has to scan its own response to find it.
/// </summary>
public sealed record MessageThreadUpdateDto(
    Guid Id,
    IReadOnlyList<string> Participants,
    int UnreadCount,
    DateTimeOffset LastMessageAt,
    int RetrievedThrough,
    int HighestSequence,
    IReadOnlyList<MessageDto> Messages)
{
    public static MessageThreadUpdateDto Create(MessageThreadUpdate source) => new(
        source.Thread.Id.Value,
        [.. source.Thread.Participants.Select(number => number.Value)],
        source.Thread.UnreadCount,
        source.Thread.LastMessageAt,
        source.Thread.RetrievedThrough,
        source.NewMessages.Max(message => message.Sequence),
        [.. source.NewMessages.Select(MessageDto.Create)]);
}

/// <summary>
/// No cursor here any more — retrieval is a server-tracked watermark per thread, advanced by
/// <see cref="AckMessageUpdatesRequestDto"/> rather than replayed by the client on the next call.
/// </summary>
public sealed record MessageUpdatesDto(IReadOnlyList<MessageThreadUpdateDto> Threads);

/// <summary>One thread's high-water sequence the caller has finished processing.</summary>
public sealed record AckThreadDto(Guid ThreadId, int ThroughSequence);

public sealed record AckMessageUpdatesRequestDto(IReadOnlyList<AckThreadDto> Threads);

/// <summary>
/// <paramref name="Threads"/> echoes each acked thread's watermark as it stands after the clamp, so a
/// human with curl can see the effective value without a second call.
/// <paramref name="UnknownThreadIds"/> lists any thread id that does not belong to this phone.
/// </summary>
public sealed record AckMessageUpdatesResponseDto(
    IReadOnlyList<AckThreadWatermarkDto> Threads,
    IReadOnlyList<Guid> UnknownThreadIds);

public sealed record AckThreadWatermarkDto(Guid ThreadId, int RetrievedThrough);
