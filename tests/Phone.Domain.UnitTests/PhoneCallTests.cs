using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Apps.Phone.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneCallTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const int RingSeconds = 30;

    private static PhoneDevice Phone(string number) =>
        PhoneDevice.Create(new PhoneDeviceProvisioned(
            new PhoneDeviceId(Guid.NewGuid()),
            PhoneNumber.Parse(number),
            "1234",
            new CharacterId(Guid.NewGuid())));

    private static PhoneCall Ringing(out PhoneDevice caller, out PhoneDevice callee)
    {
        caller = Phone("11111111");
        callee = Phone("22222222");
        return PhoneCall.Create(PhoneCall.Place(
            new PhoneCallId(Guid.NewGuid()), caller, callee, Noon, RingSeconds));
    }

    private static PhoneCall Ringing() => Ringing(out _, out _);

    [Fact]
    public void Place_StartsRingingAndRecordsBothLegs()
    {
        var call = Ringing(out var caller, out var callee);

        Assert.Equal(CallState.Ringing, call.State);
        Assert.True(call.IsActive);
        Assert.Equal(caller.Id, call.CallerPhoneId);
        Assert.Equal(callee.Id, call.CalleePhoneId);
        Assert.Equal(caller.Number, call.CallerNumber);
        Assert.Equal(callee.Number, call.CalleeNumber);
        Assert.Equal(Noon, call.PlacedAt);
        Assert.Equal(RingSeconds, call.RingSeconds);
        Assert.Null(call.AnsweredAt);
        Assert.Null(call.EndedAt);

        // The flat duplicates the ring poll filters on must agree with the ids they mirror.
        Assert.Equal(caller.Id.Value, call.CallerPhoneIdValue);
        Assert.Equal(callee.Id.Value, call.CalleePhoneIdValue);
    }

    [Fact]
    public void Place_ToItself_Throws()
    {
        var phone = Phone("11111111");

        Assert.Throws<InvalidCallTransitionException>(() => PhoneCall.Place(
            new PhoneCallId(Guid.NewGuid()), phone, phone, Noon, RingSeconds));
    }

    [Fact]
    public void Answer_MovesToAcceptedAndStampsTheAnswer()
    {
        var call = Ringing();

        call.Answer(Noon.AddSeconds(5));

        Assert.Equal(CallState.Accepted, call.State);
        Assert.True(call.IsActive);
        Assert.Equal(Noon.AddSeconds(5), call.AnsweredAt);
        Assert.Null(call.EndedAt);
    }

    [Theory]
    [InlineData(CallState.Accepted)]
    [InlineData(CallState.Rejected)]
    [InlineData(CallState.Missed)]
    [InlineData(CallState.Ended)]
    public void Answer_FromAnythingButRinging_Throws(CallState from)
    {
        var call = InState(from);

        Assert.Throws<InvalidCallTransitionException>(() => call.Answer(Noon.AddSeconds(5)));
    }

    [Theory]
    [InlineData(CallState.Accepted)]
    [InlineData(CallState.Rejected)]
    [InlineData(CallState.Missed)]
    [InlineData(CallState.Ended)]
    public void Reject_FromAnythingButRinging_Throws(CallState from)
    {
        var call = InState(from);

        Assert.Throws<InvalidCallTransitionException>(() => call.Reject(Noon.AddSeconds(5)));
    }

    [Theory]
    [InlineData(CallState.Accepted)]
    [InlineData(CallState.Rejected)]
    [InlineData(CallState.Missed)]
    [InlineData(CallState.Ended)]
    public void Miss_FromAnythingButRinging_Throws(CallState from)
    {
        var call = InState(from);

        Assert.Throws<InvalidCallTransitionException>(() => call.Miss(Noon.AddSeconds(5)));
    }

    [Theory]
    [InlineData(CallState.Ringing)]
    [InlineData(CallState.Rejected)]
    [InlineData(CallState.Missed)]
    [InlineData(CallState.Ended)]
    public void End_FromAnythingButAccepted_Throws(CallState from)
    {
        // Ringing included on purpose: hanging up on an unanswered call is Miss, not End, and the
        // endpoint is what picks between them.
        var call = InState(from);

        Assert.Throws<InvalidCallTransitionException>(() => call.End(Noon.AddSeconds(5)));
    }

    [Fact]
    public void Reject_And_Miss_BothStampEndedAtButStayDistinct()
    {
        var rejected = Ringing();
        rejected.Reject(Noon.AddSeconds(3));

        var missed = Ringing();
        missed.Miss(Noon.AddSeconds(RingSeconds));

        Assert.Equal(CallState.Rejected, rejected.State);
        Assert.Equal(CallState.Missed, missed.State);
        Assert.Equal(Noon.AddSeconds(3), rejected.EndedAt);
        Assert.Equal(Noon.AddSeconds(RingSeconds), missed.EndedAt);
        Assert.False(rejected.IsActive);
        Assert.False(missed.IsActive);
    }

    [Fact]
    public void IsExpired_IsExactAtTheBoundary()
    {
        var call = Ringing();

        Assert.False(call.IsExpired(Noon));
        Assert.False(call.IsExpired(Noon.AddSeconds(RingSeconds - 1)));

        // The window is inclusive at its end: exactly RingSeconds later it has run out.
        Assert.True(call.IsExpired(Noon.AddSeconds(RingSeconds)));
        Assert.True(call.IsExpired(Noon.AddSeconds(RingSeconds + 1)));
    }

    [Fact]
    public void IsExpired_IsFalseOnceAnswered()
    {
        // An answered call has no window — a two-hour conversation is not an expired ring.
        var call = Ringing();
        call.Answer(Noon.AddSeconds(2));

        Assert.False(call.IsExpired(Noon.AddHours(2)));
    }

    [Theory]
    [InlineData(CallState.Rejected)]
    [InlineData(CallState.Missed)]
    [InlineData(CallState.Ended)]
    public void IsExpired_IsFalseOnceTerminal(CallState from)
    {
        Assert.False(InState(from).IsExpired(Noon.AddHours(2)));
    }

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var caller = Phone("11111111");
        var callee = Phone("22222222");
        var placed = PhoneCall.Place(new PhoneCallId(Guid.NewGuid()), caller, callee, Noon, RingSeconds);

        var live = PhoneCall.Create(placed);
        var answered = live.Answer(Noon.AddSeconds(4));
        var ended = live.End(Noon.AddSeconds(90));

        var replayed = PhoneCall.Create(placed);
        replayed.Apply(answered);
        replayed.Apply(ended);

        Assert.Equal(live.State, replayed.State);
        Assert.Equal(live.AnsweredAt, replayed.AnsweredAt);
        Assert.Equal(live.EndedAt, replayed.EndedAt);
        Assert.Equal(live.RingSeconds, replayed.RingSeconds);
        Assert.Equal(live.CallerNumber, replayed.CallerNumber);
        Assert.Equal(live.CalleeNumber, replayed.CalleeNumber);
    }

    [Fact]
    public void CallState_LeavesZeroUnassigned()
    {
        // The AppKey rule: a payload missing the field must name no state rather than whichever one
        // happened to sit at 0.
        Assert.False(Enum.IsDefined((CallState)0));
        Assert.Equal(
            [CallState.Ringing, CallState.Accepted, CallState.Rejected, CallState.Missed, CallState.Ended],
            Enum.GetValues<CallState>());
    }

    private static PhoneCall InState(CallState state)
    {
        var call = Ringing();

        switch (state)
        {
            case CallState.Ringing:
                break;
            case CallState.Accepted:
                call.Answer(Noon.AddSeconds(1));
                break;
            case CallState.Rejected:
                call.Reject(Noon.AddSeconds(1));
                break;
            case CallState.Missed:
                call.Miss(Noon.AddSeconds(1));
                break;
            case CallState.Ended:
                call.Answer(Noon.AddSeconds(1));
                call.End(Noon.AddSeconds(2));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        return call;
    }
}
