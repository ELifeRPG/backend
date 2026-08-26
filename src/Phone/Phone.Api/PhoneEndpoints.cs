using ELifeRPG.Phone.Api;
using ELifeRPG.Phone.Api.Apps.Contacts;
using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Api.Sims;
using ELifeRPG.Phone.Application.Apps.Contacts;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;
using ELifeRPG.Phone.Application.Sims;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    public const string PhoneReadScope = "phone:read";
    public const string PhoneWriteScope = "phone:write";
    public const string PhoneProvisionScope = "phone:provision";
    public const string PhoneManageScope = "phone:manage";

    /// <summary>
    /// Suspend/restore sits behind its own scope so an in-game Police/State faction can be granted
    /// exactly this later without also gaining the catalog and moderation powers of phone:manage.
    /// </summary>
    public const string PhoneEnforceScope = "phone:enforce";

    private const string ReadPolicy = "Phone.Read";
    private const string WritePolicy = "Phone.Write";
    private const string ProvisionPolicy = "Phone.Provision";
    private const string ManagePolicy = "Phone.Manage";
    private const string EnforcePolicy = "Phone.Enforce";

    public static IServiceCollection AddPhoneModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPhoneInfrastructure(configuration);
        services.AddSignalR();
        services.AddSingleton<PhoneHubNotifier>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneReadScope)))
            .AddPolicy(WritePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneWriteScope)))
            .AddPolicy(ProvisionPolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneProvisionScope)))
            .AddPolicy(ManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneManageScope)))
            .AddPolicy(EnforcePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneEnforceScope)));

        return services;
    }

    public static WebApplication MapPhoneModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Phone");

        app.MapHub<PhoneHub>("hubs/phone").RequireAuthorization(WritePolicy);

        MapPhoneModels(group);
        MapDevices(group);
        MapSims(group);
        MapContacts(group);
        MapMessages(group);
        MapAdmin(group);

        return app;
    }

    private static void MapPhoneModels(RouteGroupBuilder group)
    {
        group.MapPost("phone-models", async (
                [FromBody] CreatePhoneModelRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseApps(request.SupportedApps, out var apps, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new CreatePhoneModelCommand(
                        request.DisplayName,
                        request.Tier,
                        request.ItemId is { } itemId ? new ItemId(itemId) : null,
                        request.SimSlots,
                        apps,
                        request.ContactLimit,
                        request.ThreadMessageLimit,
                        request.MaxGroupParticipants),
                    cancellationToken);

                return result switch
                {
                    CreatePhoneModelResult.Created created => Results.Ok(new { modelId = created.ModelId.Value }),
                    CreatePhoneModelResult.InvalidDefinition invalid =>
                        Results.Problem(title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(ManagePolicy)
            .WithName("CreatePhoneModel")
            .WithDescription("Defines a handset model. Tier fields decide what devices of this model can do.");

        group.MapGet("phone-models", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var models = await mediator.Send(new PhoneModelsQuery(), cancellationToken);
                return Results.Ok(models.Select(PhoneModelDto.Create));
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<PhoneModelDto>>()
            .WithName("ListPhoneModels")
            .WithDescription("Lists the handset model catalog.");
    }

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
                    ProvisionPhoneDeviceResult.Provisioned provisioned => Results.Ok(new { phoneId = provisioned.DeviceId.Value }),
                    ProvisionPhoneDeviceResult.ModelNotFound => Results.Problem(
                        title: "Phone model not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ProvisionPolicy)
            .WithName("ProvisionPhone")
            .WithDescription("Provisions a handset biolocked to a character. Ships powered off, with the model's apps installed.");

        group.MapGet("characters/{characterId:guid}/phones", async (
                Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var devices = await mediator.Send(new CharacterPhoneDevicesQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(devices.Select(PhoneDeviceDto.Create));
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<PhoneDeviceDto>>()
            .WithName("ListCharacterPhones")
            .WithDescription("Lists the handsets biolocked to a character.");

        group.MapGet("phones/{phoneId:guid}", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new PhoneDeviceLookupQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    PhoneDeviceLookupResult.Found found => Results.Ok(PhoneDeviceDto.Create(found.Device)),
                    PhoneDeviceLookupResult.NotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<PhoneDeviceDto>()
            .WithName("GetPhone")
            .WithDescription("Gets a handset.");

        group.MapPost("phones/{phoneId:guid}/power", async (
                Guid phoneId,
                [FromBody] SetPhonePowerRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new SetPhonePowerCommand(new PhoneDeviceId(phoneId), new CharacterId(request.CharacterId), request.IsPoweredOn),
                    cancellationToken);

                return result switch
                {
                    SetPhonePowerResult.PowerChanged changed => Results.Ok(new { isPoweredOn = changed.IsPoweredOn }),
                    // Not an error: a bridge retrying after a dropped response is ordinary.
                    SetPhonePowerResult.AlreadyInState already => Results.Ok(new { isPoweredOn = already.IsPoweredOn }),
                    SetPhonePowerResult.DeviceNotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                    SetPhonePowerResult.NotDeviceOwner => Results.Problem(
                        title: "Character is not bound to this device", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("SetPhonePower")
            .WithDescription("Powers a handset on or off. Powering on delivers anything queued for its SIMs.");

        group.MapGet("phones/{phoneId:guid}/apps", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new PhoneDeviceLookupQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    PhoneDeviceLookupResult.Found found => Results.Ok(
                        found.Device.InstalledApps.Select(app => new
                        {
                            key = app.Key.ToString(),
                            displayName = AppCatalog.Get(app.Key).DisplayName,
                            requiresSim = AppCatalog.Get(app.Key).RequiresSim,
                        })),
                    PhoneDeviceLookupResult.NotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("ListPhoneApps")
            .WithDescription("Lists the apps installed on a handset.");

        group.MapPut("phones/{phoneId:guid}/apps/{appKey}", async (
                Guid phoneId,
                string appKey,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseApp(appKey, out var key, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new InstallAppCommand(new PhoneDeviceId(phoneId), new CharacterId(request.CharacterId), key),
                    cancellationToken);

                return result switch
                {
                    InstallAppResult.Installed => Results.NoContent(),
                    InstallAppResult.AlreadyInstalled => Results.NoContent(),
                    InstallAppResult.NotSupportedByModel => Results.Problem(
                        title: "This model does not support that app", statusCode: StatusCodes.Status409Conflict),
                    InstallAppResult.UnknownApp => Results.Problem(
                        title: "Unknown app", statusCode: StatusCodes.Status400BadRequest),
                    InstallAppResult.DeviceNotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                    InstallAppResult.NotDeviceOwner => Results.Problem(
                        title: "Character is not bound to this device", statusCode: StatusCodes.Status403Forbidden),
                    InstallAppResult.ModelNotFound => Results.Problem(
                        title: "Device model not found", statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("InstallPhoneApp")
            .WithDescription("Installs an app on a handset. Idempotent.");

        group.MapDelete("phones/{phoneId:guid}/apps/{appKey}", async (
                Guid phoneId,
                string appKey,
                [FromQuery] Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseApp(appKey, out var key, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new UninstallAppCommand(new PhoneDeviceId(phoneId), new CharacterId(characterId), key),
                    cancellationToken);

                return result switch
                {
                    // Uninstalling loses nothing — app state lives on the SIM — so a repeat is fine.
                    UninstallAppResult.Uninstalled => Results.NoContent(),
                    UninstallAppResult.NotInstalled => Results.NoContent(),
                    UninstallAppResult.DeviceNotFound => Results.Problem(
                        title: "Device not found", statusCode: StatusCodes.Status404NotFound),
                    UninstallAppResult.NotDeviceOwner => Results.Problem(
                        title: "Character is not bound to this device", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("UninstallPhoneApp")
            .WithDescription("Uninstalls an app from a handset. Idempotent; nothing is lost.");
    }

    private static bool HasScope(AuthorizationHandlerContext context, string scope) =>
        context.User.FindFirst("scope")?.Value.Split(' ').Contains(scope) == true;

    private static bool TryParseApp(string raw, out AppKey key, out IResult? problem)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out key) && AppCatalog.Contains(key))
        {
            problem = null;
            return true;
        }

        problem = Results.Problem(
            title: $"appKey must be one of: {string.Join(", ", AppCatalog.Entries.Keys)}",
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    private static bool TryParseApps(IReadOnlyList<string> raw, out List<AppKey> apps, out IResult? problem)
    {
        apps = [];

        foreach (var candidate in raw)
        {
            if (!TryParseApp(candidate, out var key, out problem))
            {
                return false;
            }

            apps.Add(key);
        }

        problem = null;
        return true;
    }
}
