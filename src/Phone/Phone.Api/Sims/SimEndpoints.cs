using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Api.Sims;
using ELifeRPG.Phone.Application.Sims;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    private static void MapSims(RouteGroupBuilder group)
    {
        group.MapPost("sim-cards", async (
                [FromBody] ProvisionSimRequestDto request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ProvisionSimCardCommand(new CharacterId(request.CharacterId)), cancellationToken);

                return result switch
                {
                    ProvisionSimCardResult.Provisioned provisioned => Results.Ok(
                        new ProvisionSimResponseDto(provisioned.SimCardId.Value, provisioned.Number.Value)),
                    ProvisionSimCardResult.NumberExhausted => Results.Problem(
                        title: "Could not allocate a free phone number", statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            })
            .RequireAuthorization(ProvisionPolicy)
            .Produces<ProvisionSimResponseDto>()
            .WithName("ProvisionSimCard")
            .WithDescription("Issues a SIM card with a fresh number, registered to a character.");

        group.MapGet("characters/{characterId:guid}/sim-cards", async (
                Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var sims = await mediator.Send(new CharacterSimCardsQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(sims.Select(SimCardDto.Create));
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<SimCardDto>>()
            .WithName("ListCharacterSimCards")
            .WithDescription("Lists the SIM cards registered to a character.");

        group.MapGet("sim-cards/{simCardId:guid}", async (
                Guid simCardId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new SimCardLookupQuery(new SimCardId(simCardId)), cancellationToken);

                return result switch
                {
                    SimCardLookupResult.Found found => Results.Ok(SimCardDto.Create(found.SimCard)),
                    SimCardLookupResult.NotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<SimCardDto>()
            .WithName("GetSimCard")
            .WithDescription("Gets a SIM card, including its number, status and blocklist.");

        group.MapPut("phones/{phoneId:guid}/sims/{simCardId:guid}", async (
                Guid phoneId,
                Guid simCardId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new InstallSimCommand(new PhoneDeviceId(phoneId), new SimCardId(simCardId), new CharacterId(request.CharacterId)),
                    cancellationToken);

                return result switch
                {
                    InstallSimResult.Installed => Results.NoContent(),
                    InstallSimResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                    InstallSimResult.NotSimOwner => Results.Problem(
                        title: "Character does not own this SIM card", statusCode: StatusCodes.Status403Forbidden),
                    InstallSimResult.SimDeactivated => Results.Problem(
                        title: "SIM card has been deactivated", statusCode: StatusCodes.Status410Gone),
                    InstallSimResult.SimAlreadyInstalled => Results.Problem(
                        title: "SIM card is already installed in a device", statusCode: StatusCodes.Status409Conflict),
                    InstallSimResult.DeviceNotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                    InstallSimResult.NotDeviceOwner => Results.Problem(
                        title: "Character is not bound to this device", statusCode: StatusCodes.Status403Forbidden),
                    InstallSimResult.ModelNotFound => Results.Problem(
                        title: "Device model not found", statusCode: StatusCodes.Status500InternalServerError),
                    InstallSimResult.NoFreeSimSlot => Results.Problem(
                        title: "No free SIM slot on this device", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("InstallSimCard")
            .WithDescription("Seats a SIM in a handset. Contacts and message history travel with it, and anything queued is delivered.");

        group.MapDelete("phones/{phoneId:guid}/sims/{simCardId:guid}", async (
                Guid phoneId,
                Guid simCardId,
                [FromQuery] Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new EjectSimCommand(new PhoneDeviceId(phoneId), new SimCardId(simCardId), new CharacterId(characterId)),
                    cancellationToken);

                return result switch
                {
                    EjectSimResult.Ejected => Results.NoContent(),
                    EjectSimResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                    EjectSimResult.NotSimOwner => Results.Problem(
                        title: "Character does not own this SIM card", statusCode: StatusCodes.Status403Forbidden),
                    EjectSimResult.SimNotInThisDevice => Results.Problem(
                        title: "SIM card is not in this device", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("EjectSimCard")
            .WithDescription("Removes a SIM from a handset. The handset keeps nothing.");

        group.MapPost("sim-cards/{simCardId:guid}/blocks", async (
                Guid simCardId,
                [FromBody] BlockNumberRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(request.Number, out var number, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new BlockNumberCommand(new SimCardId(simCardId), new CharacterId(request.CharacterId), number),
                    cancellationToken);

                return result switch
                {
                    BlockNumberResult.Blocked => Results.NoContent(),
                    BlockNumberResult.AlreadyBlocked => Results.NoContent(),
                    BlockNumberResult.CannotBlockOwnNumber => Results.Problem(
                        title: "A SIM card can not block its own number", statusCode: StatusCodes.Status400BadRequest),
                    BlockNumberResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                    BlockNumberResult.NotSimOwner => Results.Problem(
                        title: "Character does not own this SIM card", statusCode: StatusCodes.Status403Forbidden),
                    BlockNumberResult.SimDeactivated => Results.Problem(
                        title: "SIM card has been deactivated", statusCode: StatusCodes.Status410Gone),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("BlockNumber")
            .WithDescription("Blocks a number. Blocked senders still see their messages as sent.");

        group.MapDelete("sim-cards/{simCardId:guid}/blocks/{number}", async (
                Guid simCardId,
                string number,
                [FromQuery] Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(number, out var parsed, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new UnblockNumberCommand(new SimCardId(simCardId), new CharacterId(characterId), parsed),
                    cancellationToken);

                return result switch
                {
                    UnblockNumberResult.Unblocked => Results.NoContent(),
                    UnblockNumberResult.NotBlocked => Results.NoContent(),
                    UnblockNumberResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                    UnblockNumberResult.NotSimOwner => Results.Problem(
                        title: "Character does not own this SIM card", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("UnblockNumber")
            .WithDescription("Removes a number from a SIM's blocklist.");

        group.MapPost("sim-cards/{simCardId:guid}/suspend", async (
                Guid simCardId,
                [FromBody] SuspendSimRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new SuspendSimCommand(new SimCardId(simCardId), request.Reason), cancellationToken);

                return result switch
                {
                    SuspendSimResult.Suspended => Results.NoContent(),
                    SuspendSimResult.AlreadySuspended => Results.NoContent(),
                    SuspendSimResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                    SuspendSimResult.SimDeactivated => Results.Problem(
                        title: "SIM card has been deactivated", statusCode: StatusCodes.Status410Gone),
                };
            })
            .RequireAuthorization(EnforcePolicy)
            .WithName("SuspendSimCard")
            .WithDescription("Locks a number from outside its owner's control. Messages to it are dropped, not queued; nothing stored is lost.");

        group.MapPost("sim-cards/{simCardId:guid}/restore", async (
                Guid simCardId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RestoreSimCommand(new SimCardId(simCardId)), cancellationToken);

                return result switch
                {
                    RestoreSimResult.Restored => Results.NoContent(),
                    RestoreSimResult.NotSuspended => Results.Problem(
                        title: "SIM card is not suspended", statusCode: StatusCodes.Status409Conflict),
                    RestoreSimResult.SimNotFound => Results.Problem(
                        title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(EnforcePolicy)
            .WithName("RestoreSimCard")
            .WithDescription("Lifts a suspension, returning the number with its contacts, threads and blocklist intact.");
    }
}
