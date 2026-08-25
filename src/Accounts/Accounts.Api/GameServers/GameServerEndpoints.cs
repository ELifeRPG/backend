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

        group.MapPost("", async (
                [FromBody] RegisterGameServerRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var server = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(GameServerDto.Create(server));
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<GameServerDto>()
            .WithName("RegisterGameServer")
            .WithDescription("Registers a game server in this hive. Re-registering an existing client id updates its display name and map.");

        group.MapGet("", async (
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var servers = await mediator.Send(new GameServersQuery(), cancellationToken);
                return Results.Ok(servers.Select(GameServerDto.Create).ToList());
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<List<GameServerDto>>()
            .WithName("ListGameServers")
            .WithDescription("Lists every game server registered in this hive.");

        group.MapGet("{clientId}", async (
                string clientId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GameServerLookupQuery(clientId), cancellationToken);
                return result switch
                {
                    GameServerLookupResult.Found found => Results.Ok(GameServerDto.Create(found.Server)),
                    GameServerLookupResult.NotFound => Results.Problem(
                        title: "Game server not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<GameServerDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetGameServer")
            .WithDescription("Gets a registered game server's settings by client id.");

        group.MapPatch("{clientId}", async (
                string clientId,
                [FromBody] UpdateGameServerSettingsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(clientId), cancellationToken);
                return result switch
                {
                    UpdateGameServerSettingsResult.Updated updated => Results.Ok(GameServerDto.Create(updated.Server)),
                    UpdateGameServerSettingsResult.NotFound => Results.Problem(
                        title: "Game server not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ServerAdminPolicy)
            .Produces<GameServerDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateGameServerSettings")
            .WithDescription("Partially updates a registered server's display name and map. Omitted fields are left unchanged.");

        return app;
    }
}
