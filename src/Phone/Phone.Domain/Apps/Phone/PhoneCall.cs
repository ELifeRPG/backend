using ELifeRPG.Phone.Domain.Apps.Phone.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Domain.Apps.Phone;

/// <summary>
/// One call, one stream. Event-sourced rather than a plain document like <c>PhoneSendWindow</c>,
/// because a call is history: who rang whom, when, and how it ended is exactly the call log, and it
/// is worth replaying.
///
/// The backend arbitrates state and nothing else. There is no audio here and no concept of one — the
/// mod carries the sound through the game's own voice, and this says only who is ringing whom and
/// what has happened to it. That split is why a call needs a server at all: two clients cannot agree
/// on "answered" or on who hung up first, and neither can resolve a number held on another
/// gameserver.
///
/// Expiry is deliberately not a state transition that something schedules. Nothing sweeps; a ring
/// that outlives its window is <i>computed</i> as expired by <see cref="IsExpired"/> and
/// materialised to <see cref="CallState.Missed"/> the next time anything touches it, which is the
/// same approach thread trimming and the notification cap already take in this module.
/// </summary>
public class PhoneCall
{
    public PhoneCallId Id { get; private set; }

    public PhoneDeviceId CallerPhoneId { get; private set; }

    public PhoneNumber CallerNumber { get; private set; }

    public PhoneDeviceId CalleePhoneId { get; private set; }

    public PhoneNumber CalleeNumber { get; private set; }

    /// <summary>
    /// Flat duplicates of the two ids above, carried for the same reason
    /// <c>PhoneNotification.PhoneIdValue</c> is: the ring poll reads this table on every phone every
    /// couple of seconds, so the predicate it filters on has to be a plain indexable member rather
    /// than a path through a strongly-typed struct. Written once by <see cref="Apply(CallPlaced)"/>,
    /// and a call's participants never change, so the pairs can not drift.
    /// </summary>
    public Guid CalleePhoneIdValue { get; private set; }

    /// <inheritdoc cref="CalleePhoneIdValue"/>
    public Guid CallerPhoneIdValue { get; private set; }

    public CallState State { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    /// <summary>The window this call was placed under — see <see cref="CallPlaced"/>.</summary>
    public int RingSeconds { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Ringing or Accepted. A phone with one of these is busy.</summary>
    public bool IsActive => State is CallState.Ringing or CallState.Accepted;

    public static CallPlaced Place(
        PhoneCallId id,
        PhoneDevice caller,
        PhoneDevice callee,
        DateTimeOffset placedAt,
        int ringSeconds)
    {
        if (caller.Id == callee.Id)
        {
            throw new InvalidCallTransitionException("A phone can not call itself.");
        }

        return new CallPlaced(
            id, caller.Id, caller.Number, callee.Id, callee.Number, placedAt, ringSeconds);
    }

    public static PhoneCall Create(CallPlaced domainEvent)
    {
        var call = new PhoneCall();
        call.Apply(domainEvent);
        return call;
    }

    /// <summary>
    /// True once the ring window has run out, and only while still ringing — an answered call has no
    /// window, and a terminal one has already resolved. A pure function of the two inputs so expiry
    /// is testable to the boundary without a clock.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) =>
        State == CallState.Ringing && now - PlacedAt >= TimeSpan.FromSeconds(RingSeconds);

    public CallAnswered Answer(DateTimeOffset now)
    {
        EnsureRinging("answered");

        var domainEvent = new CallAnswered(Id, now);
        Apply(domainEvent);
        return domainEvent;
    }

    public CallRejected Reject(DateTimeOffset now)
    {
        EnsureRinging("rejected");

        var domainEvent = new CallRejected(Id, now);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// The ring window ran out, or the callee's phone was off when it was placed. Separate from
    /// <see cref="Hangup"/> only in what triggers it; both land on <see cref="CallMissed"/>.
    /// </summary>
    public CallMissed Miss(DateTimeOffset now)
    {
        EnsureRinging("missed");

        var domainEvent = new CallMissed(Id, now);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Ends a call that was answered. Hanging up on one still <see cref="CallState.Ringing"/> is
    /// <see cref="Miss"/> instead, not this — the callee missed it — and the endpoint picks between
    /// the two off <see cref="State"/>, so a client pressing one button needs to know neither.
    /// </summary>
    public CallEnded End(DateTimeOffset now)
    {
        if (State != CallState.Accepted)
        {
            throw new InvalidCallTransitionException(
                $"Call {Id} is {State} and can not be ended.");
        }

        var domainEvent = new CallEnded(Id, now);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(CallPlaced domainEvent)
    {
        Id = domainEvent.Id;
        CallerPhoneId = domainEvent.CallerPhoneId;
        CallerNumber = domainEvent.CallerNumber;
        CallerPhoneIdValue = domainEvent.CallerPhoneId.Value;
        CalleePhoneId = domainEvent.CalleePhoneId;
        CalleeNumber = domainEvent.CalleeNumber;
        CalleePhoneIdValue = domainEvent.CalleePhoneId.Value;
        PlacedAt = domainEvent.PlacedAt;
        RingSeconds = domainEvent.RingSeconds;
        State = CallState.Ringing;
    }

    public void Apply(CallAnswered domainEvent)
    {
        State = CallState.Accepted;
        AnsweredAt = domainEvent.AnsweredAt;
    }

    public void Apply(CallRejected domainEvent)
    {
        State = CallState.Rejected;
        EndedAt = domainEvent.RejectedAt;
    }

    public void Apply(CallMissed domainEvent)
    {
        State = CallState.Missed;
        EndedAt = domainEvent.MissedAt;
    }

    public void Apply(CallEnded domainEvent)
    {
        State = CallState.Ended;
        EndedAt = domainEvent.EndedAt;
    }

    private void EnsureRinging(string verb)
    {
        if (State != CallState.Ringing)
        {
            throw new InvalidCallTransitionException(
                $"Call {Id} is {State} and can not be {verb}.");
        }
    }
}
