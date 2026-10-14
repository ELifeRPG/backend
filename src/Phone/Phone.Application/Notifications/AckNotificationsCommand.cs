using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Notifications;

public union AckNotificationsResult(AckNotificationsResult.Acknowledged, AckNotificationsResult.AccessDenied)
{
    public record Acknowledged;

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// Deletes the named notifications from this phone's queue. Idempotent by contract: an id already
/// gone (already acked, already trimmed by the cap) is silently ignored rather than reported, which
/// is what lets a caller retry an ack it is not sure landed with no error path to handle. An empty
/// <see cref="Ids"/> is a no-op success, not a validation failure, for the same reason.
///
/// Deliberately not the thing that clears a thread's banners on its own — see
/// <see cref="Apps.Messages.MarkThreadReadCommand"/>, which does that instead. Acking says "I have
/// pulled this down"; reading says "the player saw it", and only the latter should make a banner
/// disappear from underneath a player who has not opened the thread yet.
/// </summary>
public sealed record AckNotificationsCommand(PhoneDeviceId PhoneId, IReadOnlyList<Guid> Ids)
    : IRequest<AckNotificationsResult>;

public sealed class AckNotificationsHandler(
    IPhoneDeviceRepository phoneRepository,
    IPhoneNotificationRepository notificationRepository)
    : IRequestHandler<AckNotificationsCommand, AckNotificationsResult>
{
    public async ValueTask<AckNotificationsResult> Handle(AckNotificationsCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(request.PhoneId, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new AckNotificationsResult.AccessDenied(access);
        }

        if (request.Ids.Count > 0)
        {
            notificationRepository.Delete(request.PhoneId, request.Ids);
            await notificationRepository.SaveChangesAsync(cancellationToken);
        }

        return new AckNotificationsResult.Acknowledged();
    }
}
