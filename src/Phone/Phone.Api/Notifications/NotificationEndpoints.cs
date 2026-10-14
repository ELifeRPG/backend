using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Notifications;
using ELifeRPG.Phone.Application.Notifications;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    /// <summary>
    /// Rooted at the phone, not under /apps/: the queue is platform-level, and an app publishes into
    /// it rather than owning any part of its URL. Runs the device half of PhoneAccessPolicy rather
    /// than the app half, so a notification is reachable with every app uninstalled, and refused only
    /// when the phone itself is not usable.
    /// </summary>
    private static void MapNotifications(RouteGroupBuilder group)
    {
        group.MapGet("phones/{phoneId:guid}/notifications", async (
                Guid phoneId, [FromQuery] string? appKey, IMediator mediator, CancellationToken cancellationToken) =>
            {
                AppKey? parsedAppKey = null;
                if (appKey is not null)
                {
                    if (!TryParseApp(appKey, out var key, out var problem))
                    {
                        return problem!;
                    }

                    parsedAppKey = key;
                }

                var result = await mediator.Send(
                    new PhoneNotificationsQuery(new PhoneDeviceId(phoneId), parsedAppKey), cancellationToken);

                return result switch
                {
                    PhoneNotificationsResult.Notifications notifications => Results.Ok(
                        new PhoneNotificationsDto([.. notifications.Entries.Select(PhoneNotificationDto.Create)])),
                    PhoneNotificationsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<PhoneNotificationsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("ListNotifications")
            .WithDescription("Lists a phone's undelivered notifications, oldest first. Optionally filter to one app with ?appKey=. Reachable with the named app uninstalled — this is platform state, not app state — but refused like any other operation on a powered-off or enforced phone.");

        group.MapPost("phones/{phoneId:guid}/notifications/ack", async (
                Guid phoneId, [FromBody] AckNotificationsRequestDto request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new AckNotificationsCommand(new PhoneDeviceId(phoneId), request.Ids), cancellationToken);

                return result switch
                {
                    AckNotificationsResult.Acknowledged => Results.NoContent(),
                    AckNotificationsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("AckNotifications")
            .WithDescription("Removes the given notifications from the queue. Idempotent — an unknown or already-acked id is ignored rather than reported, so a caller unsure whether an earlier ack landed can simply resend it.");
    }
}
