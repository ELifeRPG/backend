using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Notifications;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Application.Apps.Phone;

public union PlaceCallResult(
    PlaceCallResult.Ringing,
    PlaceCallResult.Undeliverable,
    PlaceCallResult.Unanswered,
    PlaceCallResult.Busy,
    PlaceCallResult.CallingSelf,
    PlaceCallResult.AlreadyOnACall,
    PlaceCallResult.AccessDenied)
{
    /// <summary>
    /// Carries the call rather than a copy of its fields: the Api layer needs both legs' phone ids
    /// to push the ring to the right hub group, and one mapper rather than two to render it.
    /// </summary>
    public record Ringing(PhoneCall Call);

    /// <summary>
    /// A number nobody holds, or one the state has locked or retired — what a real network reports.
    /// A blocked caller never sees this: see <see cref="Unanswered"/>.
    /// </summary>
    public record Undeliverable(PhoneNumber To);

    /// <summary>
    /// It rang nowhere and the caller may not know why. Two different situations collapse into this
    /// one answer on purpose: the callee's phone was off, or the caller is on the callee's blocklist.
    /// Reporting those apart would make the API a block-detector, which is the same rule the send
    /// path follows by reporting a blocked message as sent.
    ///
    /// A call is recorded either way, so the callee still finds a missed call when they power on —
    /// except for a block, where nothing is recorded at all.
    /// </summary>
    public record Unanswered(PhoneNumber To);

    public record Busy(PhoneNumber To);

    public record CallingSelf;

    /// <summary>The caller is already on a call. Hang that one up first.</summary>
    public record AlreadyOnACall(PhoneCallId CallId);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record PlaceCallCommand(PhoneDeviceId PhoneId, PhoneNumber To) : IRequest<PlaceCallResult>;

/// <summary>
/// Dials a number. The reachability decision mirrors <c>SendMessageCommand</c>'s recipient loop
/// deliberately, including its silences — the two apps should not disagree about what a dead,
/// suspended or blocking number does.
///
/// Where it differs is what cannot be deferred: a text to an unreachable phone becomes a
/// <c>PendingDelivery</c> and arrives later, while a call to one is simply missed. A two-hour-old
/// ring is noise, so there is no queue here and no flush.
///
/// Do Not Disturb is not consulted. It is stored beside the home layout and the wallpaper as a
/// presentation preference the mod owns, nothing server-side reads it, and it does not silence
/// message banners either — so silencing calls alone would be inconsistent, and would also hide from
/// the caller that the phone rang at all.
/// </summary>
public sealed class PlaceCallHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneCallRepository callRepository,
    IPhoneNotificationRepository notificationRepository,
    TimeProvider timeProvider,
    IMediator mediator)
    : IRequestHandler<PlaceCallCommand, PlaceCallResult>
{
    public async ValueTask<PlaceCallResult> Handle(PlaceCallCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Phone, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new PlaceCallResult.AccessDenied(access);
        }

        var caller = granted.Phone;
        if (request.To == caller.Number)
        {
            return new PlaceCallResult.CallingSelf();
        }

        var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Expire first, so a caller whose own last call rang out is not told they are still on it.
        if (await ResolveActiveAsync(caller.Id, now, settings.PhoneNotificationLimit, cancellationToken) is { } ownCall)
        {
            return new PlaceCallResult.AlreadyOnACall(ownCall.Id);
        }

        var callee = await phoneRepository.FindByNumberAsync(request.To, cancellationToken);
        if (callee is null || callee.Status != PhoneStatus.Active)
        {
            return new PlaceCallResult.Undeliverable(request.To);
        }

        if (callee.IsBlocked(caller.Number))
        {
            // Nothing is recorded and nothing rings. The caller is told only that it went
            // unanswered, which is also what a powered-off phone looks like from their side.
            return new PlaceCallResult.Unanswered(request.To);
        }

        if (await ResolveActiveAsync(callee.Id, now, settings.PhoneNotificationLimit, cancellationToken) is not null)
        {
            return new PlaceCallResult.Busy(request.To);
        }

        var placed = PhoneCall.Place(
            new PhoneCallId(Guid.NewGuid()), caller, callee, now, settings.PhoneRingSeconds);
        var call = PhoneCall.Create(placed);
        callRepository.StartStream(call, placed);

        // Powered off, or the dialer uninstalled: there is nothing to ring, so the call is placed and
        // immediately missed. The record and the banner are what the callee finds when they power on,
        // which is the dialer's equivalent of the queue Messages keeps.
        if (!callee.IsPoweredOn || !callee.HasApp(AppKey.Phone))
        {
            await CallExpiry.MissAsync(
                call, callRepository, notificationRepository, settings.PhoneNotificationLimit, now, cancellationToken);
            await callRepository.SaveChangesAsync(cancellationToken);

            return new PlaceCallResult.Unanswered(request.To);
        }

        await callRepository.SaveChangesAsync(cancellationToken);
        return new PlaceCallResult.Ringing(call);
    }

    /// <summary>
    /// The phone's active call, with a rung-out one materialised to Missed on the way past. This is
    /// the whole of expiry: nothing sweeps, so every path that cares has to resolve it, and this is
    /// the only place that knows how.
    /// </summary>
    private async ValueTask<PhoneCall?> ResolveActiveAsync(
        PhoneDeviceId phoneId, DateTimeOffset now, int notificationLimit, CancellationToken cancellationToken)
    {
        var call = await callRepository.FindActiveForPhoneAsync(phoneId, cancellationToken);
        if (call is null)
        {
            return null;
        }

        if (!call.IsExpired(now))
        {
            return call;
        }

        await CallExpiry.MissAsync(
            call, callRepository, notificationRepository, notificationLimit, now, cancellationToken);
        return null;
    }
}
