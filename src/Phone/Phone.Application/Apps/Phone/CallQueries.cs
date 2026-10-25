using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Apps.Phone;

public union RingingCallsResult(RingingCallsResult.Calls, RingingCallsResult.AccessDenied)
{
    public record Calls(IReadOnlyList<PhoneCall> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// What is ringing this phone right now — the dialer's delivery surface, and the one read in this
/// module a client is expected to hit every couple of seconds while its dialer is open.
///
/// Deliberately not shaped like <c>MessageUpdatesQuery</c>. That one loads every thread a phone has
/// and filters in memory, which is the simpler trade when a phone holds a handful of threads and the
/// poll is leisurely. Ringing is neither: it is latency-critical, so the filter belongs in the
/// database against an index, and the result is bounded by "one active call per phone" rather than by
/// a phone's whole history.
///
/// It also expires in passing. A ring whose window has closed is materialised to Missed here rather
/// than reported as still ringing, which is what makes the poll the de-facto expiry sweep without
/// anything being scheduled.
/// </summary>
public sealed record RingingCallsQuery(PhoneDeviceId PhoneId) : IRequest<RingingCallsResult>;

public sealed class RingingCallsHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneCallRepository callRepository,
    IPhoneNotificationRepository notificationRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<RingingCallsQuery, RingingCallsResult>
{
    public async ValueTask<RingingCallsResult> Handle(RingingCallsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Phone, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new RingingCallsResult.AccessDenied(access);
        }

        var ringing = await callRepository.FindRingingForPhoneAsync(request.PhoneId, cancellationToken);
        if (ringing.Count == 0)
        {
            return new RingingCallsResult.Calls([]);
        }

        var now = timeProvider.GetUtcNow();
        var expired = ringing.Where(call => call.IsExpired(now)).ToList();
        if (expired.Count == 0)
        {
            return new RingingCallsResult.Calls(ringing);
        }

        // Every expired ring here shares one callee — this phone — so the banners go to the cap
        // helper together. Publishing them one at a time would under-count the cap against what this
        // same uncommitted session had already added, which is the trap PhoneNotifications documents.
        var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);

        foreach (var call in expired)
        {
            callRepository.Append(call.Id, call.Miss(now));
        }

        await PhoneNotifications.PublishAsync(
            notificationRepository,
            request.PhoneId,
            [.. expired.Select(call => CallExpiry.MissedNotification(call, now))],
            settings.PhoneNotificationLimit,
            cancellationToken);

        await callRepository.SaveChangesAsync(cancellationToken);

        return new RingingCallsResult.Calls([.. ringing.Except(expired)]);
    }
}
