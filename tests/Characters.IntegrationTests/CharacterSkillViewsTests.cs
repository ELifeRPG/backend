using ELifeRPG.Characters.Application.Skills;
using ELifeRPG.Characters.Domain.Skills;
using Xunit;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Pure-function tests for CharacterSkillViews.BuildFullState — no infra needed, unlike the
/// other classes in this project (BuildFullState only touches SkillCatalog/SkillLeveling).
/// </summary>
public class CharacterSkillViewsTests
{
    [Fact]
    public void BuildFullState_EmptyDictionary_ReturnsAllTenSkillsAtLevelOneZeroXp()
    {
        var result = CharacterSkillViews.BuildFullState(new Dictionary<SkillType, long>());

        Assert.Equal(10, result.Count);
        Assert.All(result, view => Assert.Equal(1, view.Level));
        Assert.All(result, view => Assert.Equal(0, view.TotalXp));
    }

    [Fact]
    public void BuildFullState_WithSomeXp_ReflectsCorrectLevelAndCategoryForThatSkill()
    {
        var totalXpBySkill = new Dictionary<SkillType, long> { [SkillType.Mining] = 500 };

        var result = CharacterSkillViews.BuildFullState(totalXpBySkill);

        var mining = Assert.Single(result, v => v.Skill == SkillType.Mining);
        Assert.Equal(500, mining.TotalXp);
        Assert.Equal(SkillCategory.Gathering, mining.Category);
        Assert.Equal(SkillLeveling.LevelForTotalXp(500), mining.Level);
    }

    [Fact]
    public void BuildFullState_UntouchedSkills_DefaultToZeroXpAlongsideATouchedSkill()
    {
        var totalXpBySkill = new Dictionary<SkillType, long> { [SkillType.Mining] = 500 };

        var result = CharacterSkillViews.BuildFullState(totalXpBySkill);

        Assert.Contains(result, v => v.Skill == SkillType.Cooking && v.TotalXp == 0 && v.Level == 1);
    }

    [Fact]
    public void BuildFullState_SkillAtMaxLevel_ReportsZeroXpForNextLevel()
    {
        var totalXpBySkill = new Dictionary<SkillType, long> { [SkillType.Mining] = long.MaxValue / 2 };

        var result = CharacterSkillViews.BuildFullState(totalXpBySkill);

        var mining = Assert.Single(result, v => v.Skill == SkillType.Mining);
        Assert.Equal(SkillLeveling.MaxLevel, mining.Level);
        Assert.Equal(0, mining.XpForNextLevel);
    }
}
