using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Domain.Devices;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    private static void MapDevices(RouteGroupBuilder group)
    {
        group.MapPost("phones", async (
                [FromBody] ProvisionPhoneRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);

                return result switch
                {
                    ProvisionPhoneResult.Provisioned provisioned => Results.Ok(
                        new ProvisionPhoneResponseDto(provisioned.PhoneId.Value, provisioned.Number.Value)),
                    ProvisionPhoneResult.InvalidPin invalid => Results.Problem(
                        title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                    ProvisionPhoneResult.NumberExhausted => Results.Problem(
                        title: "Could not allocate a free phone number", statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            })
            .RequireAuthorization(ProvisionPolicy)
            .Produces<ProvisionPhoneResponseDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithName("ProvisionPhone")
            .WithDescription("Provisions a phone with a fresh number and a PIN, registered to a character. Ships powered off, with every app installed.");

        group.MapGet("characters/{characterId:guid}/phones", async (
                Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var phones = await mediator.Send(new CharacterPhonesQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(phones.Select(PhoneDto.Create));
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<PhoneDto>>()
            .WithName("ListCharacterPhones")
            .WithDescription("Lists the phones registered to a character. A character may hold several, each with its own number.");

        group.MapGet("phones/{phoneId:guid}", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new PhoneDeviceLookupQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    PhoneDeviceLookupResult.Found found => Results.Ok(PhoneDto.Create(found.Phone)),
                    PhoneDeviceLookupResult.NotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<PhoneDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetPhone")
            .WithDescription("Gets a phone, including its number, status and blocklist. Never its PIN.");

        group.MapPost("phones/{phoneId:guid}/power", async (
                Guid phoneId,
                [FromBody] SetPhonePowerRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new SetPhonePowerCommand(new PhoneDeviceId(phoneId), request.ToActor(), request.IsPoweredOn),
                    cancellationToken);

                return result switch
                {
                    SetPhonePowerResult.PowerChanged changed => Results.Ok(new SetPhonePowerResponseDto(changed.IsPoweredOn)),
                    // Not an error: a bridge retrying after a dropped response is ordinary.
                    SetPhonePowerResult.AlreadyInState already => Results.Ok(new SetPhonePowerResponseDto(already.IsPoweredOn)),
                    SetPhonePowerResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                    SetPhonePowerResult.NotAuthorized => NotAuthorized(),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<SetPhonePowerResponseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetPhonePower")
            .WithDescription("Powers a phone on or off. Powering on delivers anything queued for its number.");

        group.MapPost("phones/{phoneId:guid}/pin", async (
                Guid phoneId,
                [FromBody] ChangePinRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ChangePinCommand(new PhoneDeviceId(phoneId), request.ToActor(), request.NewPin),
                    cancellationToken);

                return result switch
                {
                    ChangePinResult.Changed => Results.NoContent(),
                    ChangePinResult.InvalidPin invalid => Results.Problem(
                        title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                    ChangePinResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                    ChangePinResult.NotAuthorized => NotAuthorized(),
                    ChangePinResult.PhoneDeactivated => Results.Problem(
                        title: "Phone has been deactivated", statusCode: StatusCodes.Status410Gone),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("ChangePhonePin")
            .WithDescription("Sets a new PIN. Takes the owner, or the current PIN from whoever else is holding the phone.");

        group.MapGet("phones/{phoneId:guid}/apps", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new PhoneDeviceLookupQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    PhoneDeviceLookupResult.Found found => Results.Ok(
                        found.Phone.InstalledApps.Select(app => new PhoneAppDto(
                            app.Key.ToString(),
                            AppCatalog.Get(app.Key).DisplayName))),
                    PhoneDeviceLookupResult.NotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<PhoneAppDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListPhoneApps")
            .WithDescription("Lists the apps installed on a phone.");

        group.MapPut("phones/{phoneId:guid}/apps/{appKey}", async (
                Guid phoneId,
                string appKey,
                [FromBody] PhoneActorRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseApp(appKey, out var key, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new InstallAppCommand(new PhoneDeviceId(phoneId), request.ToActor(), key),
                    cancellationToken);

                return result switch
                {
                    InstallAppResult.Installed => Results.NoContent(),
                    InstallAppResult.AlreadyInstalled => Results.NoContent(),
                    InstallAppResult.UnknownApp => Results.Problem(
                        title: "Unknown app", statusCode: StatusCodes.Status400BadRequest),
                    InstallAppResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                    InstallAppResult.NotAuthorized => NotAuthorized(),
                    InstallAppResult.PhoneDeactivated => Results.Problem(
                        title: "Phone has been deactivated", statusCode: StatusCodes.Status410Gone),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("InstallPhoneApp")
            .WithDescription("Installs an app. Every phone can run every app; installing Messages delivers anything queued while it was gone. Idempotent.");

        group.MapDelete("phones/{phoneId:guid}/apps/{appKey}", async (
                Guid phoneId,
                string appKey,
                [FromQuery] Guid characterId,
                [FromQuery] string? pin,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseApp(appKey, out var key, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new UninstallAppCommand(new PhoneDeviceId(phoneId), new PhoneActor(new CharacterId(characterId), pin), key),
                    cancellationToken);

                return result switch
                {
                    // Uninstalling loses nothing — contacts and threads are the phone's — so a repeat is fine.
                    UninstallAppResult.Uninstalled => Results.NoContent(),
                    UninstallAppResult.NotInstalled => Results.NoContent(),
                    UninstallAppResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                    UninstallAppResult.NotAuthorized => NotAuthorized(),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UninstallPhoneApp")
            .WithDescription("Uninstalls an app. Idempotent; nothing is lost, and messages queue rather than vanish.");
    }

    private static void MapEnforcement(RouteGroupBuilder group)
    {
        group.MapPost("phones/{phoneId:guid}/suspend", async (
                Guid phoneId,
                [FromBody] SuspendPhoneRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new SuspendPhoneCommand(new PhoneDeviceId(phoneId), request.Reason), cancellationToken);

                return result switch
                {
                    SuspendPhoneResult.Suspended => Results.NoContent(),
                    SuspendPhoneResult.AlreadySuspended => Results.NoContent(),
                    SuspendPhoneResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                    SuspendPhoneResult.PhoneDeactivated => Results.Problem(
                        title: "Phone has been deactivated", statusCode: StatusCodes.Status410Gone),
                };
            })
            .RequireAuthorization(EnforcePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("SuspendPhone")
            .WithDescription("Locks a number from outside its owner's control. It can neither send nor receive, and messages to it are dropped rather than queued. Nothing stored is lost.");

        group.MapPost("phones/{phoneId:guid}/restore", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RestorePhoneCommand(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    RestorePhoneResult.Restored => Results.NoContent(),
                    RestorePhoneResult.NotSuspended => Results.Problem(
                        title: "Phone is not suspended", statusCode: StatusCodes.Status409Conflict),
                    RestorePhoneResult.PhoneNotFound => Results.Problem(
                        title: "Phone not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(EnforcePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RestorePhone")
            .WithDescription("Lifts a suspension, handing the number back whole. A deactivated phone stays retired.");
    }

    /// <summary>
    /// One status and one wording for "not the owner" and "wrong PIN" alike — the platform commands'
    /// counterpart to PhoneAccessProblem's NotAuthorized case, and deliberately the same text, so
    /// neither surface can be used to work out whose phone this is.
    /// </summary>
    private static IResult NotAuthorized() => Results.Problem(
        title: "Not the phone's owner, and no matching PIN was supplied",
        statusCode: StatusCodes.Status403Forbidden);
}
