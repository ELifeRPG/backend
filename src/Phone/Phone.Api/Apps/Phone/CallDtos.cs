using ELifeRPG.Phone.Application.Apps.Phone;
using ELifeRPG.Phone.Domain.Apps.Phone;

namespace ELifeRPG.Phone.Api.Apps.Phone;

/// <summary>
/// A call as a client sees it. Both numbers, because a dialer shows who is on the other end and the
/// queue's rule applies here too — the bare number crosses the wire and the mod resolves a name from
/// Contacts itself.
///
/// <paramref name="State"/> crosses as a string, like every other enum in this module, so the
/// persisted ordinal stays an implementation detail. <paramref name="ExpiresAt"/> is sent rather than
/// the window length, so a client never has to add a duration to a timestamp of its own and get the
/// clock skew wrong; a call that is not ringing has none.
/// </summary>
public sealed record PhoneCallDto(
    Guid CallId,
    string From,
    string To,
    string State,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt)
{
    public static PhoneCallDto Create(PhoneCall source) => new(
        source.Id.Value,
        source.CallerNumber.Value,
        source.CalleeNumber.Value,
        source.State.ToString(),
        source.PlacedAt,
        source.State == CallState.Ringing ? source.PlacedAt.AddSeconds(source.RingSeconds) : null,
        source.AnsweredAt,
        source.EndedAt);
}

public sealed record PlaceCallRequestDto(string To);

/// <summary>
/// What dialling produced. <paramref name="CallId"/> is null when nothing is ringing — an
/// unreachable number, or one that went unanswered — because there is no call for the caller to
/// answer, reject or hang up.
/// </summary>
public sealed record PlaceCallResponseDto(Guid? CallId, string To, string Outcome, DateTimeOffset? ExpiresAt);

public sealed record CallStateResponseDto(Guid CallId, string State);
