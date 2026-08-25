namespace ELifeRPG.Characters.Api.Skills;

public sealed record RecordSkillActionResponseDto
{
    public required IReadOnlyList<SkillXpGrantDto> Gains { get; init; }

    public required IReadOnlyList<CharacterSkillDto> Skills { get; init; }

    public static RecordSkillActionResponseDto Create(RecordSkillActionResult.Recorded source) => new()
    {
        Gains = source.Gains.Select(SkillXpGrantDto.Create).ToList(),
        Skills = source.FullState.Select(CharacterSkillDto.Create).ToList(),
    };
}
