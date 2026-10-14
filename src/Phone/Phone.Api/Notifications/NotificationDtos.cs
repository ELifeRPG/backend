using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Api.Notifications;

/// <summary>
/// <paramref name="AppKey"/> crosses the wire as a string, matching <c>PhoneAppDto</c> elsewhere in
/// this module — the persisted ordinal stays an implementation detail.
/// </summary>
public sealed record PhoneNotificationDto(
    Guid Id,
    string AppKey,
    string Category,
    string GroupKey,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Payload,
    DateTimeOffset OccurredAt)
{
    public static PhoneNotificationDto Create(PhoneNotification source) => new(
        source.Id,
        source.AppKey.ToString(),
        source.Category,
        source.GroupKey,
        source.Title,
        source.Body,
        source.Payload,
        source.OccurredAt);
}

public sealed record PhoneNotificationsDto(IReadOnlyList<PhoneNotificationDto> Notifications);

/// <summary>
/// An id already gone — already acked, already trimmed by the cap — is silently ignored rather than
/// reported, which is what lets a caller retry an ack it is unsure landed with no error path to
/// handle. Returns 204: Marten reports no affected-row count for a bulk delete, so an "acknowledged:
/// N" field on the response would be a claim this endpoint cannot actually back up.
/// </summary>
public sealed record AckNotificationsRequestDto(IReadOnlyList<Guid> Ids);
