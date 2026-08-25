namespace ELifeRPG.Characters.Api.Skills;

public sealed record CharacterSkillDto
{
    public required string Skill { get; init; }

    public required string Category { get; init; }

    public required long TotalXp { get; init; }

    public required int Level { get; init; }

    public required long XpForNextLevel { get; init; }

    public static CharacterSkillDto Create(CharacterSkillView source) => new()
    {
        Skill = source.Skill.ToString(),
        Category = source.Category.ToString(),
        TotalXp = source.TotalXp,
        Level = source.Level,
        XpForNextLevel = source.XpForNextLevel,
    };
}
