using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Api.Apps.Messages;

public sealed record MessageDto(Guid Id, string From, string Body, DateTimeOffset SentAt, bool IsOutbound)
{
    public static MessageDto Create(Message source) =>
        new(source.Id.Value, source.From.Value, source.Body, source.SentAt, source.IsOutbound);
}

/// <summary>
/// The message list is omitted from the thread-list projection — a phone's inbox shows participants,
/// the unread count and a timestamp, and only the opened thread needs its bodies.
/// </summary>
public sealed record MessageThreadSummaryDto(
    Guid Id,
    IReadOnlyList<string> Participants,
    int UnreadCount,
    DateTimeOffset LastMessageAt)
{
    public static MessageThreadSummaryDto Create(MessageThread source) => new(
        source.Id.Value,
        [.. source.Participants.Select(number => number.Value)],
        source.UnreadCount,
        source.LastMessageAt);
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
/// plus only those messages that arrived after the caller's cursor — not the thread's whole history.
/// </summary>
public sealed record MessageThreadUpdateDto(
    Guid Id,
    IReadOnlyList<string> Participants,
    int UnreadCount,
    DateTimeOffset LastMessageAt,
    IReadOnlyList<MessageDto> Messages)
{
    public static MessageThreadUpdateDto Create(MessageThreadUpdate source) => new(
        source.Thread.Id.Value,
        [.. source.Thread.Participants.Select(number => number.Value)],
        source.Thread.UnreadCount,
        source.Thread.LastMessageAt,
        [.. source.NewMessages.Select(MessageDto.Create)]);
}

/// <summary>
/// <paramref name="PolledAt"/> is the cursor to send back as <c>since</c> on the next poll. Holding
/// on to it is the whole protocol; a client that loses it polls without one and gets everything.
/// </summary>
public sealed record MessageUpdatesDto(DateTimeOffset PolledAt, IReadOnlyList<MessageThreadUpdateDto> Threads);
