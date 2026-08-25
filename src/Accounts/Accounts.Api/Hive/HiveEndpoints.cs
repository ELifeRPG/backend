using ELifeRPG.Accounts.Api.Common;
using ELifeRPG.Accounts.Api.Hive;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class HiveModule
{
    private const string ServerAdminPolicy = "Hive.Admin";

    public static IServiceCollection AddHiveModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ServerAdminPolicy, policy => policy.RequireAssertion(context =>
                RealmRoleAuthorization.HasRole(context.User, GameServerModule.ServerAdminRole)));

        return services;
    }

    public static WebApplication MapHiveModule(this WebApplication app)
    {
        var group = app.MapGroup("api/hive").WithTags("Hive");

        group.MapGet("settings", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var settings = await mediator.Send(new HiveSettingsQuery(), cancellationToken);
                return Results.Ok(HiveSettingsDto.Create(settings));
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<HiveSettingsDto>()
            .WithName("GetHiveSettings")
            .WithDescription("Gets this hive's deployment-wide settings.");

        group.MapPatch("settings", async (
                [FromBody] UpdateHiveSettingsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var settings = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(HiveSettingsDto.Create(settings));
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<HiveSettingsDto>()
            .WithName("UpdateHiveSettings")
            .WithDescription("Partially updates this hive's settings. Omitted fields are left unchanged.");

        return app;
    }
}
