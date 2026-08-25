namespace ELifeRPG.Characters.Api.Skills;

public sealed record SkillXpGrantDto
{
    public required string Skill { get; init; }

    public required long XpGained { get; init; }

    public required long NewTotalXp { get; init; }

    public required int NewLevel { get; init; }

    public required bool DidLevelUp { get; init; }

    public static SkillXpGrantDto Create(SkillXpGrant source) => new()
    {
        Skill = source.Skill.ToString(),
        XpGained = source.XpGained,
        NewTotalXp = source.NewTotalXp,
        NewLevel = source.NewLevel,
        DidLevelUp = source.DidLevelUp,
    };
}
