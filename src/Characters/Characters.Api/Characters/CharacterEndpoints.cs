using ELifeRPG.Characters.Api.Characters;
using ELifeRPG.Characters.Api.Common;
using ELifeRPG.Characters.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class CharacterModule
{
    public const string CharactersWriteScope = "gameserver:characters:write";
    private const string CharactersWritePolicy = "Characters.Write";

    public static IServiceCollection AddCharacterModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCharacterInfrastructure(configuration);
        services.AddScoped<ICurrentGameServer, HttpContextCurrentGameServer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(CharactersWritePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(CharactersWriteScope)));

        return services;
    }

    public static WebApplication MapCharacterModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Characters");

        group.MapPost("characters", async (
                [FromBody] CreateCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);

                return result switch
                {
                    CreateCharacterResult.Created created => Results.Ok(CharacterDto.Create(created, request.Name)),
                    CreateCharacterResult.AccountNotFound => Results.Problem(
                        title: "Account not found",
                        statusCode: StatusCodes.Status404NotFound),
                    CreateCharacterResult.AccountLocked => Results.Problem(
                        title: "Account locked",
                        detail: "This account is currently locked. Contact the server owner.",
                        statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(CharactersWritePolicy)
            .Produces<CharacterDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("CreateCharacter")
            .WithDescription("Creates a new character for an account.");

        group.MapGet("accounts/{accountId:guid}/characters", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var characters = await mediator.Send(new CharactersQuery(new AccountId(accountId)), cancellationToken);
                return Results.Ok(characters.Select(CharacterDto.Create).ToList());
            })
            .RequireAuthorization(CharactersWritePolicy)
            .Produces<List<CharacterDto>>()
            .WithName("ListAccountCharacters")
            .WithDescription("Lists characters for an account.");

        group.MapPost("characters/{characterId:guid}/sessions", async (
                Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new StartCharacterSessionCommand(new CharacterId(characterId)), cancellationToken);

                return result switch
                {
                    StartCharacterSessionResult.Started => Results.Ok(),
                    StartCharacterSessionResult.CharacterNotFound => Results.Problem(
                        title: "Character not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(CharactersWritePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("StartCharacterSession")
            .WithDescription("Starts a character's in-game session, e.g. when a player selects this character. Starting again while already active simply supersedes the previous session — there's no gameserver-crash/restart cleanup yet, so a stale active flag must not permanently block reselecting.");

        group.MapDelete("characters/{characterId:guid}/sessions", async (
                Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new EndCharacterSessionCommand(new CharacterId(characterId)), cancellationToken);

                return result switch
                {
                    EndCharacterSessionResult.Ended => Results.Ok(),
                    EndCharacterSessionResult.CharacterNotFound => Results.Problem(
                        title: "Character not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(CharactersWritePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("EndCharacterSession")
            .WithDescription("Ends a character's in-game session, e.g. on player disconnect.");

        return app;
    }
}
