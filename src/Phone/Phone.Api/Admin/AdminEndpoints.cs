using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Application.Admin;
using ELifeRPG.Phone.Domain.Devices;
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
        group.MapGet("admin/phones", async (
                [FromQuery] string? number, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var phones = await mediator.Send(new ModerationPhonesQuery(number), cancellationToken);
                return Results.Ok(phones.Select(PhoneDto.Create));
            })
            .RequireAuthorization(ManagePolicy)
            .Produces<IEnumerable<PhoneDto>>()
            .WithName("AdminListPhones")
            .WithDescription("Staff: lists phones, optionally filtered by number fragment. PINs are not returned.");

        group.MapGet("admin/phones/{phoneId:guid}/threads", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var threads = await mediator.Send(new ModerationThreadsQuery(new PhoneDeviceId(phoneId)), cancellationToken);
                return Results.Ok(threads.Select(MessageThreadDto.Create));
            })
            .RequireAuthorization(ManagePolicy)
            .Produces<IEnumerable<MessageThreadDto>>()
            .WithName("AdminListPhoneThreads")
            .WithDescription("Staff: reads a phone's conversations.");
    }
}
