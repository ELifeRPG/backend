namespace ELifeRPG.Characters.Api.Skills;

public sealed record GrantSkillXpRequestDto
{
    public required string Skill { get; init; }

    public required long Amount { get; init; }

    public GrantSkillXpCommand ToCommand(Guid characterId) => new(new CharacterId(characterId), Skill, Amount);
}
