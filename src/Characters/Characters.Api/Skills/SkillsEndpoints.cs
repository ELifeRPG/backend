using ELifeRPG.Characters.Api.Skills;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

internal static class SkillsEndpoints
{
    public static RouteGroupBuilder MapSkillsEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("characters/{characterId:guid}/skills/actions", async (
                Guid characterId,
                [FromBody] RecordSkillActionRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(characterId), cancellationToken);

                return result switch
                {
                    RecordSkillActionResult.Recorded recorded => Results.Ok(RecordSkillActionResponseDto.Create(recorded)),
                    RecordSkillActionResult.CharacterNotFound => Results.Problem(
                        title: "Character not found",
                        statusCode: StatusCodes.Status404NotFound),
                    RecordSkillActionResult.UnknownAction => Results.Problem(
                        title: "Unknown skill action",
                        statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(CharacterModule.SkillsWritePolicy)
            .Produces<RecordSkillActionResponseDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("RecordSkillAction")
            .WithDescription("Reports a gameplay action for a character, granting XP to every skill the action's catalog entry rewards, scaled by Quantity. Repeated occurrences of the same action should be coalesced into Quantity rather than calling this once per micro-action.");

        group.MapPost("characters/{characterId:guid}/skills/xp", async (
                Guid characterId,
                [FromBody] GrantSkillXpRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(characterId), cancellationToken);

                return result switch
                {
                    GrantSkillXpResult.Granted granted => Results.Ok(GrantSkillXpResponseDto.Create(granted)),
                    GrantSkillXpResult.CharacterNotFound => Results.Problem(
                        title: "Character not found",
                        statusCode: StatusCodes.Status404NotFound),
                    GrantSkillXpResult.UnknownSkill => Results.Problem(
                        title: "Unknown skill",
                        statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(CharacterModule.SkillsManagePolicy)
            .Produces<GrantSkillXpResponseDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GrantSkillXp")
            .WithDescription("Directly grants skill XP, bypassing the action catalog. Staff/admin-only correction path, recorded with XpSource.ManualGrant for audit.");

        group.MapGet("characters/{characterId:guid}/skills", async (
                Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var skills = await mediator.Send(new CharacterSkillsQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(skills.Select(CharacterSkillDto.Create).ToList());
            })
            .RequireAuthorization(CharacterModule.SkillsWritePolicy)
            .Produces<List<CharacterSkillDto>>()
            .WithName("GetCharacterSkills")
            .WithDescription("Lists every skill's level/XP for a character, defaulting untouched skills to level 1 with 0 XP.");

        group.MapGet("skills", () =>
                Results.Ok(SkillCatalog.Entries.Select(entry => SkillCatalogEntryDto.Create(entry.Key, entry.Value)).ToList()))
            .RequireAuthorization()
            .Produces<List<SkillCatalogEntryDto>>()
            .WithName("GetSkillCatalog")
            .WithDescription("Static catalog of every known skill, its category, and display name.");

        return group;
    }
}
