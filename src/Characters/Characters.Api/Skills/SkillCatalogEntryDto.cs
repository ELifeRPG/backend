namespace ELifeRPG.Characters.Api.Skills;

public sealed record SkillCatalogEntryDto
{
    public required string Skill { get; init; }

    public required string Category { get; init; }

    public required string DisplayName { get; init; }

    public static SkillCatalogEntryDto Create(SkillType skill, SkillCatalogEntry entry) => new()
    {
        Skill = skill.ToString(),
        Category = entry.Category.ToString(),
        DisplayName = entry.DisplayName,
    };
}
