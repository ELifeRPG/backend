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

public sealed record BlockNumberRequestDto(Guid CharacterId, string Number, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

public sealed record SendMessageRequestDto(Guid CharacterId, IReadOnlyList<string> To, string Body, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

/// <summary>
/// <paramref name="UndeliverableRecipients"/> reports only what a real network would reveal —
/// numbers that do not exist, or are suspended or retired. A blocked recipient is deliberately
/// absent: the sender must not be able to detect a block.
/// </summary>
public sealed record SendMessageResponseDto(Guid ThreadId, Guid MessageId, IReadOnlyList<string> UndeliverableRecipients);
