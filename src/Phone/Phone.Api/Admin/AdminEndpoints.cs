using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Api.Sims;
using ELifeRPG.Phone.Application.Admin;
using ELifeRPG.Phone.Domain.Sims;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    private static void MapAdmin(RouteGroupBuilder group)
    {
        group.MapGet("admin/sim-cards", async (
                [FromQuery] string? number, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var sims = await mediator.Send(new ModerationSimCardsQuery(number), cancellationToken);
                return Results.Ok(sims.Select(SimCardDto.Create));
            })
            .RequireAuthorization(ManagePolicy)
            .Produces<IEnumerable<SimCardDto>>()
            .WithName("AdminSearchSimCards")
            .WithDescription("Staff: searches SIM cards by number fragment.");

        group.MapGet("admin/phones", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var devices = await mediator.Send(new ModerationDevicesQuery(), cancellationToken);
                return Results.Ok(devices.Select(PhoneDeviceDto.Create));
            })
            .RequireAuthorization(ManagePolicy)
            .Produces<IEnumerable<PhoneDeviceDto>>()
            .WithName("AdminListPhones")
            .WithDescription("Staff: lists handsets.");

        group.MapGet("admin/sim-cards/{simCardId:guid}/threads", async (
                Guid simCardId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var threads = await mediator.Send(new ModerationThreadsQuery(new SimCardId(simCardId)), cancellationToken);
                return Results.Ok(threads.Select(MessageThreadDto.Create));
            })
            .RequireAuthorization(ManagePolicy)
            .Produces<IEnumerable<MessageThreadDto>>()
            .WithName("AdminListSimThreads")
            .WithDescription("Staff: reads a SIM's conversations.");
    }
}
