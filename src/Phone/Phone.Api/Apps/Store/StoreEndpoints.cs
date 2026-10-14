using ELifeRPG.Phone.Api.Apps.Store;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Application.Apps.Store;
using ELifeRPG.Phone.Domain.Devices;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    /// <summary>
    /// The one app whose routes are not under <c>/apps/{its key}/</c>: an app store reads across the
    /// whole catalog rather than owning a noun of its own, and <c>/app-store/</c> says that plainly
    /// where <c>/apps/store/apps</c> only repeated the word. It is still the Store app — the guard
    /// chain runs against <see cref="AppKey.Store"/> like any other app operation.
    /// </summary>
    private static void MapStore(RouteGroupBuilder group)
    {
        group.MapGet("phones/{phoneId:guid}/app-store/apps", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new StoreAppsQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    StoreAppsResult.Apps apps => Results.Ok(apps.Entries.Select(StoreAppDto.Create)),
                    StoreAppsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<StoreAppDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("ListStoreApps")
            .WithDescription("Lists the apps the store offers, and whether this phone already has each one. The phone's own system apps are not offered — they can be neither installed nor removed from here.");
    }
}
