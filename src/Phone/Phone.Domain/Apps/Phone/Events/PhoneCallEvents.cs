using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Phone.Events;

/// <summary>
/// Both numbers ride on the event alongside both ids, because a call is history and the numbers are
/// what a call log shows — resolving them from the phones at read time would rewrite the log if a
/// number were ever reassigned.
///
/// <paramref name="RingSeconds"/> rides on it for the reason <c>OutboundMessageRecorded</c> carries
/// its retention limit: the window that applied is a fact about the moment the call was placed, so a
/// staff member changing <c>HiveSettings.PhoneRingSeconds</c> cannot retroactively decide whether an
/// old call had already expired.
/// </summary>
public sealed record CallPlaced(
    PhoneCallId Id,
    PhoneDeviceId CallerPhoneId,
    PhoneNumber CallerNumber,
    PhoneDeviceId CalleePhoneId,
    PhoneNumber CalleeNumber,
    DateTimeOffset PlacedAt,
    int RingSeconds);

public sealed record CallAnswered(PhoneCallId Id, DateTimeOffset AnsweredAt);

public sealed record CallRejected(PhoneCallId Id, DateTimeOffset RejectedAt);

/// <summary>
/// Raised by three different triggers that mean the same thing to the callee: the ring window ran
/// out, the caller hung up before it was answered, or the callee's phone was off when it was placed.
/// </summary>
public sealed record CallMissed(PhoneCallId Id, DateTimeOffset MissedAt);

public sealed record CallEnded(PhoneCallId Id, DateTimeOffset EndedAt);
