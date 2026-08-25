using ELifeRPG.Characters.Domain.Skills;
using Xunit;

namespace ELifeRPG.Characters.Domain.UnitTests;

public class SkillLevelingTests
{
    [Fact]
    public void LevelForTotalXp_ZeroXp_IsLevelOne()
    {
        Assert.Equal(1, SkillLeveling.LevelForTotalXp(0));
    }

    [Fact]
    public void LevelForTotalXp_JustBelowThreshold_StaysAtCurrentLevel()
    {
        var xpForLevelTwo = SkillLeveling.XpForNextLevel(1);

        Assert.Equal(1, SkillLeveling.LevelForTotalXp(xpForLevelTwo - 1));
    }

    [Fact]
    public void LevelForTotalXp_ExactlyAtThreshold_LevelsUp()
    {
        var xpForLevelTwo = SkillLeveling.XpForNextLevel(1);

        Assert.Equal(2, SkillLeveling.LevelForTotalXp(xpForLevelTwo));
    }

    [Fact]
    public void LevelForTotalXp_HugeTotal_CapsAtMaxLevel()
    {
        Assert.Equal(SkillLeveling.MaxLevel, SkillLeveling.LevelForTotalXp(long.MaxValue / 2));
    }

    [Fact]
    public void XpForNextLevel_UsesGentleExponentialCurve()
    {
        Assert.Equal(105, SkillLeveling.XpForNextLevel(1));
        Assert.Equal(100, SkillLeveling.XpForNextLevel(0));
    }
}
