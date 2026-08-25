namespace ELifeRPG.Characters.Api.Skills;

public sealed record RecordSkillActionRequestDto
{
    public required string Action { get; init; }

    public int? Quantity { get; init; }

    public RecordSkillActionCommand ToCommand(Guid characterId) => new(new CharacterId(characterId), Action, Quantity ?? 1);
}
