namespace ELifeRPG.Characters.Domain.Skills;

public sealed record SkillCatalogEntry(SkillCategory Category, string DisplayName);

public static class SkillCatalog
{
    public static readonly IReadOnlyDictionary<SkillType, SkillCatalogEntry> Entries = new Dictionary<SkillType, SkillCatalogEntry>
    {
        [SkillType.Mining] = new(SkillCategory.Gathering, "Mining"),
        [SkillType.Woodcutting] = new(SkillCategory.Gathering, "Woodcutting"),
        [SkillType.Fishing] = new(SkillCategory.Gathering, "Fishing"),
        [SkillType.Farming] = new(SkillCategory.Gathering, "Farming"),
        [SkillType.Scavenging] = new(SkillCategory.Gathering, "Scavenging"),
        [SkillType.Blacksmithing] = new(SkillCategory.Crafting, "Blacksmithing"),
        [SkillType.Carpentry] = new(SkillCategory.Crafting, "Carpentry"),
        [SkillType.Cooking] = new(SkillCategory.Crafting, "Cooking"),
        [SkillType.Tailoring] = new(SkillCategory.Crafting, "Tailoring"),
        [SkillType.Engineering] = new(SkillCategory.Crafting, "Engineering"),
    };
}
