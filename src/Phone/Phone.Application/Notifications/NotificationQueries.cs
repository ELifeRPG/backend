using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Notifications;

public union PhoneNotificationsResult(PhoneNotificationsResult.Notifications, PhoneNotificationsResult.AccessDenied)
{
    public record Notifications(IReadOnlyList<PhoneNotification> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// A null <paramref name="AppKey"/> means every app's notifications; naming one filters to it. Runs
/// the device chain, not the app chain — notifications are platform-level and readable with the
/// filtered app itself uninstalled, exactly as an uninstalled Messages still queues deliveries.
/// </summary>
public sealed record PhoneNotificationsQuery(PhoneDeviceId PhoneId, AppKey? AppKey) : IRequest<PhoneNotificationsResult>;

public sealed class PhoneNotificationsHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneNotificationRepository notificationRepository)
    : IRequestHandler<PhoneNotificationsQuery, PhoneNotificationsResult>
{
    public async ValueTask<PhoneNotificationsResult> Handle(PhoneNotificationsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(request.PhoneId, phoneRepository, cancellationToken);

        return access is PhoneAccessResult.Granted
            ? new PhoneNotificationsResult.Notifications(
                await notificationRepository.FindForPhoneAsync(request.PhoneId, request.AppKey, cancellationToken))
            : new PhoneNotificationsResult.AccessDenied(access);
    }
}
