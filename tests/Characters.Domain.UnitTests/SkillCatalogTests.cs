using ELifeRPG.Characters.Domain.Skills;
using Xunit;

namespace ELifeRPG.Characters.Domain.UnitTests;

public class SkillCatalogTests
{
    [Fact]
    public void Entries_ContainsExactlyTenSkillsAcrossGatheringAndCrafting()
    {
        Assert.Equal(10, SkillCatalog.Entries.Count);
        Assert.Equal(5, SkillCatalog.Entries.Count(e => e.Value.Category == SkillCategory.Gathering));
        Assert.Equal(5, SkillCatalog.Entries.Count(e => e.Value.Category == SkillCategory.Crafting));
    }

    [Theory]
    [InlineData(SkillType.Mining, SkillCategory.Gathering, "Mining")]
    [InlineData(SkillType.Woodcutting, SkillCategory.Gathering, "Woodcutting")]
    [InlineData(SkillType.Fishing, SkillCategory.Gathering, "Fishing")]
    [InlineData(SkillType.Farming, SkillCategory.Gathering, "Farming")]
    [InlineData(SkillType.Scavenging, SkillCategory.Gathering, "Scavenging")]
    [InlineData(SkillType.Blacksmithing, SkillCategory.Crafting, "Blacksmithing")]
    [InlineData(SkillType.Carpentry, SkillCategory.Crafting, "Carpentry")]
    [InlineData(SkillType.Cooking, SkillCategory.Crafting, "Cooking")]
    [InlineData(SkillType.Tailoring, SkillCategory.Crafting, "Tailoring")]
    [InlineData(SkillType.Engineering, SkillCategory.Crafting, "Engineering")]
    public void Entries_MapsEachSkillToExpectedCategoryAndDisplayName(SkillType skill, SkillCategory expectedCategory, string expectedDisplayName)
    {
        var entry = SkillCatalog.Entries[skill];

        Assert.Equal(expectedCategory, entry.Category);
        Assert.Equal(expectedDisplayName, entry.DisplayName);
    }
}
