using ELifeRPG.Accounts.Api.Common;
using ELifeRPG.Accounts.Api.GameServers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class GameServerModule
{
    public const string ServerAdminRole = "server-admin";
    private const string ServerAdminPolicy = "GameServers.Admin";

    public static IServiceCollection AddGameServerModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ServerAdminPolicy, policy => policy.RequireAssertion(context =>
                RealmRoleAuthorization.HasRole(context.User, ServerAdminRole)));

        return services;
    }

    public static WebApplication MapGameServerModule(this WebApplication app)
    {
        var group = app.MapGroup("api/game-servers").WithTags("GameServers");

        group.MapGet("{clientId}", async (
                string clientId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var server = await mediator.Send(new GameServerLookupQuery(clientId), cancellationToken);
                return Results.Ok(GameServerDto.Create(server));
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<GameServerDto>()
            .WithName("GetGameServer")
            .WithDescription("Gets a server's settings, defaulted if never configured.");

        group.MapPatch("{clientId}", async (
                string clientId,
                [FromBody] UpdateGameServerSettingsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var server = await mediator.Send(request.ToCommand(clientId), cancellationToken);
                return Results.Ok(GameServerDto.Create(server));
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<GameServerDto>()
            .WithName("UpdateGameServerSettings")
            .WithDescription("Partially updates a server's settings (e.g. WhitelistEnabled). Omitted fields are left unchanged.");

        return app;
    }
}
