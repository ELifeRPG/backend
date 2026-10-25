using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Apps.Phone;

public union AnswerCallResult(
    AnswerCallResult.Answered,
    AnswerCallResult.CallNotFound,
    AnswerCallResult.NotRinging,
    AnswerCallResult.NotTheCallee,
    AnswerCallResult.AccessDenied)
{
    public record Answered(PhoneCall Call);

    public record CallNotFound;

    /// <summary>Already answered, rejected, missed or over — including rung out while unanswered.</summary>
    public record NotRinging(CallState State);

    /// <summary>Only the phone being rung may answer or reject.</summary>
    public record NotTheCallee;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record AnswerCallCommand(PhoneDeviceId PhoneId, PhoneCallId CallId) : IRequest<AnswerCallResult>;

public sealed class AnswerCallHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneCallRepository callRepository,
    IPhoneNotificationRepository notificationRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<AnswerCallCommand, AnswerCallResult>
{
    public async ValueTask<AnswerCallResult> Handle(AnswerCallCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Phone, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new AnswerCallResult.AccessDenied(access);
        }

        var call = await callRepository.FindByIdAsync(request.CallId, cancellationToken);
        if (call is null)
        {
            return new AnswerCallResult.CallNotFound();
        }

        if (call.CalleePhoneId != request.PhoneId)
        {
            return new AnswerCallResult.NotTheCallee();
        }

        var now = timeProvider.GetUtcNow();

        // A ring that already ran out is missed, not answerable — the race between the window
        // closing and the player's thumb resolves the same way whoever gets there first.
        if (call.IsExpired(now))
        {
            await CallExpiry.MissAsync(
                call, callRepository, notificationRepository, mediator, now, cancellationToken);
            await callRepository.SaveChangesAsync(cancellationToken);
            return new AnswerCallResult.NotRinging(CallState.Missed);
        }

        if (call.State != CallState.Ringing)
        {
            return new AnswerCallResult.NotRinging(call.State);
        }

        callRepository.Append(call.Id, call.Answer(now));
        await callRepository.SaveChangesAsync(cancellationToken);

        return new AnswerCallResult.Answered(call);
    }
}

public union RejectCallResult(
    RejectCallResult.Rejected,
    RejectCallResult.CallNotFound,
    RejectCallResult.NotRinging,
    RejectCallResult.NotTheCallee,
    RejectCallResult.AccessDenied)
{
    public record Rejected(PhoneCall Call);

    public record CallNotFound;

    public record NotRinging(CallState State);

    public record NotTheCallee;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record RejectCallCommand(PhoneDeviceId PhoneId, PhoneCallId CallId) : IRequest<RejectCallResult>;

/// <summary>
/// The callee declining. Distinct from the window running out, and distinct from the caller giving
/// up: all three stop the ringing, but only this one is a decision, and a call log that conflated
/// them would lose the only interesting thing about it.
/// </summary>
public sealed class RejectCallHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneCallRepository callRepository,
    IPhoneNotificationRepository notificationRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<RejectCallCommand, RejectCallResult>
{
    public async ValueTask<RejectCallResult> Handle(RejectCallCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Phone, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new RejectCallResult.AccessDenied(access);
        }

        var call = await callRepository.FindByIdAsync(request.CallId, cancellationToken);
        if (call is null)
        {
            return new RejectCallResult.CallNotFound();
        }

        if (call.CalleePhoneId != request.PhoneId)
        {
            return new RejectCallResult.NotTheCallee();
        }

        var now = timeProvider.GetUtcNow();

        if (call.IsExpired(now))
        {
            await CallExpiry.MissAsync(
                call, callRepository, notificationRepository, mediator, now, cancellationToken);
            await callRepository.SaveChangesAsync(cancellationToken);
            return new RejectCallResult.NotRinging(CallState.Missed);
        }

        if (call.State != CallState.Ringing)
        {
            return new RejectCallResult.NotRinging(call.State);
        }

        callRepository.Append(call.Id, call.Reject(now));
        await callRepository.SaveChangesAsync(cancellationToken);

        return new RejectCallResult.Rejected(call);
    }
}

public union EndCallResult(
    EndCallResult.Ended,
    EndCallResult.CallNotFound,
    EndCallResult.AlreadyOver,
    EndCallResult.NotAParticipant,
    EndCallResult.AccessDenied)
{
    /// <summary>
    /// The call's state says what hanging up meant: Ended for one that had been answered, Missed for
    /// one still ringing — hanging up before an answer is a call the other side missed.
    /// </summary>
    public record Ended(PhoneCall Call);

    public record CallNotFound;

    public record AlreadyOver(CallState State);

    /// <summary>Either leg may hang up, but only those two.</summary>
    public record NotAParticipant;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record EndCallCommand(PhoneDeviceId PhoneId, PhoneCallId CallId) : IRequest<EndCallResult>;

/// <summary>
/// Either party putting the phone down. What it means depends on whether the call had been answered,
/// and the caller pressing one button should not have to know which — so this picks between
/// <c>PhoneCall.End</c> and <c>PhoneCall.Miss</c> off the current state.
/// </summary>
public sealed class EndCallHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneCallRepository callRepository,
    IPhoneNotificationRepository notificationRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<EndCallCommand, EndCallResult>
{
    public async ValueTask<EndCallResult> Handle(EndCallCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Phone, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new EndCallResult.AccessDenied(access);
        }

        var call = await callRepository.FindByIdAsync(request.CallId, cancellationToken);
        if (call is null)
        {
            return new EndCallResult.CallNotFound();
        }

        if (call.CalleePhoneId != request.PhoneId && call.CallerPhoneId != request.PhoneId)
        {
            return new EndCallResult.NotAParticipant();
        }

        var now = timeProvider.GetUtcNow();

        if (!call.IsActive)
        {
            return new EndCallResult.AlreadyOver(call.State);
        }

        if (call.State == CallState.Ringing)
        {
            // Rung out or hung up on — either way the callee missed it, and either way they get the
            // banner, so expiry needs no separate branch here.
            await CallExpiry.MissAsync(
                call, callRepository, notificationRepository, mediator, now, cancellationToken);
            await callRepository.SaveChangesAsync(cancellationToken);
            return new EndCallResult.Ended(call);
        }

        callRepository.Append(call.Id, call.End(now));
        await callRepository.SaveChangesAsync(cancellationToken);

        return new EndCallResult.Ended(call);
    }
}

/// <summary>
/// The one place that knows how a ringing call becomes a missed one, shared by every path that can
/// observe the window having closed. Nothing sweeps, so a call expires the next time anything looks
/// at it — the same approach thread trimming and the notification cap already take here.
/// </summary>
internal static class CallExpiry
{
    /// <summary>
    /// For callers that have not already read <c>HiveSettings</c> — the three state-change handlers,
    /// which need the cap only on the branch where they find an expired ring.
    /// </summary>
    public static async ValueTask MissAsync(
        PhoneCall call,
        IPhoneCallRepository callRepository,
        IPhoneNotificationRepository notificationRepository,
        IMediator mediator,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);
        await MissAsync(
            call, callRepository, notificationRepository, settings.PhoneNotificationLimit, now, cancellationToken);
    }

    public static async ValueTask MissAsync(
        PhoneCall call,
        IPhoneCallRepository callRepository,
        IPhoneNotificationRepository notificationRepository,
        int notificationLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        callRepository.Append(call.Id, call.Miss(now));

        await PhoneNotifications.PublishAsync(
            notificationRepository,
            call.CalleePhoneId,
            [MissedNotification(call, now)],
            notificationLimit,
            cancellationToken);
    }

    /// <summary>
    /// The banner, composed but not published, so a caller with several expired rings for one phone
    /// can hand them to <c>PhoneNotifications.PublishAsync</c> in one batch — publishing them one at
    /// a time would under-count the cap against whatever the same uncommitted session already added.
    ///
    /// The title is the bare caller number and the body is empty on purpose: the queue's contract
    /// forbids resolving a display name server-side, and "Missed call" is the mod's wording to
    /// choose, not the platform's.
    /// </summary>
    public static PhoneNotification MissedNotification(PhoneCall call, DateTimeOffset occurredAt) =>
        PhoneNotification.Create(
            call.CalleePhoneId,
            AppKey.Phone,
            category: "call.missed",
            groupKey: call.CallerNumber.Value,
            title: call.CallerNumber.Value,
            body: string.Empty,
            payload: new Dictionary<string, string>
            {
                ["callId"] = call.Id.Value.ToString(),
                ["from"] = call.CallerNumber.Value,
            },
            occurredAt: occurredAt);
}
