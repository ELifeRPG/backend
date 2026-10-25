using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Apps.Phone;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Notifications;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Requires the local infra stack. Covers the dialer: the reachability decision, the state machine
/// across two phones, and the lazy expiry that stands in for a background sweep.
///
/// The clock is driven by hand — see <see cref="TestClock"/> — because the ring window is thirty
/// seconds by default and every expiry assertion would otherwise be a thirty-second wait.
/// </summary>
public sealed class CallCommandTests : IAsyncLifetime
{
    private const string Pin = "1234";

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(services => services.AddSingleton<TimeProvider>(_clock));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private async Task<T> Send<T>(IRequest<T> request)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, CancellationToken.None);
    }

    private static void ExpectCase(bool matched, string expected, object actual) =>
        Assert.True(matched, $"Expected {expected}, got {actual}");

    private async Task<Phone> SetUpPhone()
    {
        var owner = new CharacterId(Guid.NewGuid());

        var result = await Send(new ProvisionPhoneCommand(owner, Pin));
        if (result is not ProvisionPhoneResult.Provisioned provisioned)
        {
            throw new XunitException($"Expected Provisioned, got {result}");
        }

        await Send(new SetPhonePowerCommand(provisioned.PhoneId, new PhoneActor(owner), true));
        return new Phone(owner, provisioned.PhoneId, provisioned.Number);
    }

    private async Task<Phone> ProvisionOnly()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var result = await Send(new ProvisionPhoneCommand(owner, Pin));
        if (result is not ProvisionPhoneResult.Provisioned provisioned)
        {
            throw new XunitException($"Expected Provisioned, got {result}");
        }

        return new Phone(owner, provisioned.PhoneId, provisioned.Number);
    }

    private sealed record Phone(CharacterId Owner, PhoneDeviceId Id, PhoneNumber Number)
    {
        public PhoneActor Actor => new(Owner);
    }

    private async Task<PhoneCallId> DialRinging(Phone from, Phone to)
    {
        var result = await Send(new PlaceCallCommand(from.Id, to.Number));
        if (result is not PlaceCallResult.Ringing ringing)
        {
            throw new XunitException($"Expected Ringing, got {result}");
        }

        return ringing.Call.Id;
    }

    private async Task<IReadOnlyList<PhoneCall>> Ringing(Phone phone)
    {
        var result = await Send(new RingingCallsQuery(phone.Id));
        return result is RingingCallsResult.Calls calls
            ? calls.Entries
            : throw new XunitException($"Expected Calls, got {result}");
    }

    private async Task<IReadOnlyList<PhoneNotification>> Notifications(Phone phone)
    {
        var result = await Send(new PhoneNotificationsQuery(phone.Id, AppKey.Phone));
        return result is PhoneNotificationsResult.Notifications notifications
            ? notifications.Entries
            : throw new XunitException($"Expected Notifications, got {result}");
    }

    private async Task<T> WithRingSeconds<T>(int seconds, Func<Task<T>> body)
    {
        var original = (await Send(new HiveSettingsQuery())).PhoneRingSeconds;
        await Send(new UpdateHiveSettingsCommand(null, PhoneRingSeconds: seconds));
        try
        {
            return await body();
        }
        finally
        {
            await Send(new UpdateHiveSettingsCommand(null, PhoneRingSeconds: original));
        }
    }

    // ---------- Dialling ----------

    [Fact]
    public async Task PlaceCall_RingsTheCalleeAndNobodyElse()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var bystander = await SetUpPhone();

        var callId = await DialRinging(caller, callee);

        var ringing = Assert.Single(await Ringing(callee));
        Assert.Equal(callId, ringing.Id);
        Assert.Equal(CallState.Ringing, ringing.State);
        Assert.Equal(caller.Number, ringing.CallerNumber);

        // The poll is per callee, so the caller's own outgoing call must not come back as incoming.
        Assert.Empty(await Ringing(caller));
        Assert.Empty(await Ringing(bystander));
    }

    [Fact]
    public async Task PlaceCall_ToAnUnknownNumber_IsUndeliverable()
    {
        var caller = await SetUpPhone();

        var result = await Send(new PlaceCallCommand(caller.Id, PhoneNumber.Parse("99999999")));

        ExpectCase(result is PlaceCallResult.Undeliverable, "Undeliverable", result);
    }

    [Fact]
    public async Task PlaceCall_ToASuspendedPhone_IsUndeliverable()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        await Send(new SuspendPhoneCommand(callee.Id, "Police order"));

        var result = await Send(new PlaceCallCommand(caller.Id, callee.Number));

        ExpectCase(result is PlaceCallResult.Undeliverable, "Undeliverable", result);
    }

    [Fact]
    public async Task PlaceCall_ToABlockingPhone_LooksUnansweredAndNeverRings()
    {
        // The send path's rule, carried over: the caller must not be able to tell a block from a
        // phone nobody picked up, so the two answer identically and nothing is recorded.
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        await Send(new BlockNumberCommand(callee.Id, caller.Number));

        var result = await Send(new PlaceCallCommand(caller.Id, callee.Number));

        ExpectCase(result is PlaceCallResult.Unanswered, "Unanswered", result);
        Assert.Empty(await Ringing(callee));
        Assert.Empty(await Notifications(callee));
    }

    [Fact]
    public async Task PlaceCall_ToAPoweredOffPhone_IsMissedWithABanner()
    {
        // Unlike a text, nothing is queued — a two-hour-old ring is noise. What survives is the
        // missed call and its banner, which is what the callee finds when they power on.
        var caller = await SetUpPhone();
        var callee = await ProvisionOnly();

        var result = await Send(new PlaceCallCommand(caller.Id, callee.Number));
        ExpectCase(result is PlaceCallResult.Unanswered, "Unanswered", result);

        await Send(new SetPhonePowerCommand(callee.Id, callee.Actor, true));

        Assert.Empty(await Ringing(callee));

        var banner = Assert.Single(await Notifications(callee));
        Assert.Equal("call.missed", banner.Category);
        Assert.Equal(caller.Number.Value, banner.Title);
        Assert.Equal(caller.Number.Value, banner.GroupKey);
        Assert.Equal(caller.Number.Value, banner.Payload["from"]);
    }

    [Fact]
    public async Task PlaceCall_ToItself_IsRefused()
    {
        var phone = await SetUpPhone();

        var result = await Send(new PlaceCallCommand(phone.Id, phone.Number));

        ExpectCase(result is PlaceCallResult.CallingSelf, "CallingSelf", result);
    }

    [Fact]
    public async Task PlaceCall_ToAPhoneAlreadyRinging_IsBusy()
    {
        var first = await SetUpPhone();
        var second = await SetUpPhone();
        var callee = await SetUpPhone();
        await DialRinging(first, callee);

        var result = await Send(new PlaceCallCommand(second.Id, callee.Number));

        ExpectCase(result is PlaceCallResult.Busy, "Busy", result);
    }

    [Fact]
    public async Task PlaceCall_WhileAlreadyOnACall_IsRefused()
    {
        var caller = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();
        await DialRinging(caller, first);

        var result = await Send(new PlaceCallCommand(caller.Id, second.Number));

        ExpectCase(result is PlaceCallResult.AlreadyOnACall, "AlreadyOnACall", result);
    }

    // ---------- The state machine ----------

    [Fact]
    public async Task AnswerCall_ThenEnd_RoundTrips()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        var answered = await Send(new AnswerCallCommand(callee.Id, callId));
        if (answered is not AnswerCallResult.Answered accepted)
        {
            throw new XunitException($"Expected Answered, got {answered}");
        }

        Assert.Equal(CallState.Accepted, accepted.Call.State);
        Assert.Empty(await Ringing(callee));

        var ended = await Send(new EndCallCommand(caller.Id, callId));
        if (ended is not EndCallResult.Ended over)
        {
            throw new XunitException($"Expected Ended, got {ended}");
        }

        Assert.Equal(CallState.Ended, over.Call.State);
    }

    [Fact]
    public async Task RejectCall_IsRecordedAsRejectedRatherThanMissed()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        var result = await Send(new RejectCallCommand(callee.Id, callId));
        if (result is not RejectCallResult.Rejected rejected)
        {
            throw new XunitException($"Expected Rejected, got {result}");
        }

        Assert.Equal(CallState.Rejected, rejected.Call.State);
        Assert.Empty(await Ringing(callee));

        // Declining is a decision, not an absence, so it leaves no missed-call banner.
        Assert.Empty(await Notifications(callee));
    }

    [Fact]
    public async Task EndCall_WhileStillRinging_LeavesTheCalleeAMissedCall()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        var result = await Send(new EndCallCommand(caller.Id, callId));
        if (result is not EndCallResult.Ended ended)
        {
            throw new XunitException($"Expected Ended, got {result}");
        }

        Assert.Equal(CallState.Missed, ended.Call.State);
        Assert.Single(await Notifications(callee));
    }

    [Fact]
    public async Task AnswerCall_ByAnyoneButTheCallee_IsRefused()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var bystander = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        ExpectCase(
            await Send(new AnswerCallCommand(caller.Id, callId)) is AnswerCallResult.NotTheCallee,
            "NotTheCallee",
            "the caller answering its own call");

        var byBystander = await Send(new AnswerCallCommand(bystander.Id, callId));
        ExpectCase(byBystander is AnswerCallResult.NotTheCallee, "NotTheCallee", byBystander);
    }

    [Fact]
    public async Task AnswerCall_Twice_IsRefusedTheSecondTime()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);
        await Send(new AnswerCallCommand(callee.Id, callId));

        var again = await Send(new AnswerCallCommand(callee.Id, callId));

        ExpectCase(again is AnswerCallResult.NotRinging, "NotRinging", again);
    }

    [Fact]
    public async Task EndCall_ByAPhoneOnNeitherLeg_IsRefused()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var bystander = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        var result = await Send(new EndCallCommand(bystander.Id, callId));

        ExpectCase(result is EndCallResult.NotAParticipant, "NotAParticipant", result);
    }

    [Fact]
    public async Task AnswerCall_WithAnUnknownCallId_IsNotFound()
    {
        var phone = await SetUpPhone();

        var result = await Send(new AnswerCallCommand(phone.Id, new PhoneCallId(Guid.NewGuid())));

        ExpectCase(result is AnswerCallResult.CallNotFound, "CallNotFound", result);
    }

    // ---------- Expiry, without a background job ----------

    [Fact]
    public async Task RingingCalls_PastTheWindow_ReportsNothingAndRecordsAMissedCall()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        Assert.Single(await Ringing(callee));

        var settings = await Send(new HiveSettingsQuery());
        _clock.Advance(TimeSpan.FromSeconds(settings.PhoneRingSeconds));

        // The poll is the sweep: it reports nothing, and the call is Missed by the time it returns.
        Assert.Empty(await Ringing(callee));

        var banner = Assert.Single(await Notifications(callee));
        Assert.Equal("call.missed", banner.Category);
        Assert.Equal(callId.Value.ToString(), banner.Payload["callId"]);

        // And it stays missed rather than being re-missed on every later poll.
        Assert.Empty(await Ringing(callee));
        Assert.Single(await Notifications(callee));
    }

    [Fact]
    public async Task AnswerCall_AfterTheWindowClosed_IsMissedRatherThanConnected()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        var callId = await DialRinging(caller, callee);

        var settings = await Send(new HiveSettingsQuery());
        _clock.Advance(TimeSpan.FromSeconds(settings.PhoneRingSeconds));

        var result = await Send(new AnswerCallCommand(callee.Id, callId));
        if (result is not AnswerCallResult.NotRinging notRinging)
        {
            throw new XunitException($"Expected NotRinging, got {result}");
        }

        Assert.Equal(CallState.Missed, notRinging.State);
        Assert.Single(await Notifications(callee));
    }

    [Fact]
    public async Task PlaceCall_AfterTheCallersOwnCallRangOut_IsAllowedAgain()
    {
        // Expiry has to be resolved on the caller's side too, or one unanswered call would wedge a
        // phone as permanently busy.
        var caller = await SetUpPhone();
        var first = await SetUpPhone();
        var second = await SetUpPhone();
        await DialRinging(caller, first);

        var settings = await Send(new HiveSettingsQuery());
        _clock.Advance(TimeSpan.FromSeconds(settings.PhoneRingSeconds));

        var result = await Send(new PlaceCallCommand(caller.Id, second.Number));

        ExpectCase(result is PlaceCallResult.Ringing, "Ringing", result);
    }

    [Fact]
    public async Task PhoneRingSeconds_IsCarriedOnTheCallRatherThanReadAtExpiry()
    {
        // Lowering the knob must not retroactively expire a call already ringing under the old one.
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();

        var callId = await WithRingSeconds(600, async () =>
        {
            var id = await DialRinging(caller, callee);
            return id;
        });

        // Back at the default (30s) and advanced past it, but this call was placed under 600.
        _clock.Advance(TimeSpan.FromSeconds(60));

        var ringing = Assert.Single(await Ringing(callee));
        Assert.Equal(callId, ringing.Id);
        Assert.Equal(600, ringing.RingSeconds);
    }

    // ---------- The guard chain ----------

    [Fact]
    public async Task PlaceCall_OnAPoweredOffPhone_IsRefusedLikeAnyAppOperation()
    {
        var caller = await ProvisionOnly();
        var callee = await SetUpPhone();

        var result = await Send(new PlaceCallCommand(caller.Id, callee.Number));

        if (result is not PlaceCallResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhonePoweredOff, "PhonePoweredOff", denied.Reason);
    }

    [Fact]
    public async Task RingingCalls_OnAPoweredOffPhone_IsRefused()
    {
        var phone = await ProvisionOnly();

        var result = await Send(new RingingCallsQuery(phone.Id));

        if (result is not RingingCallsResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhonePoweredOff, "PhonePoweredOff", denied.Reason);
    }

    [Fact]
    public async Task PlaceCall_FromASuspendedPhone_IsRefused()
    {
        var caller = await SetUpPhone();
        var callee = await SetUpPhone();
        await Send(new SuspendPhoneCommand(caller.Id, "Police order"));

        var result = await Send(new PlaceCallCommand(caller.Id, callee.Number));

        if (result is not PlaceCallResult.AccessDenied denied)
        {
            throw new XunitException($"Expected AccessDenied, got {result}");
        }

        ExpectCase(denied.Reason is PhoneAccessResult.PhoneSuspended, "PhoneSuspended", denied.Reason);
    }
}
