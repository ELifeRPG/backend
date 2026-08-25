using ELifeRPG.Characters.Domain.Skills;
using Xunit;

namespace ELifeRPG.Characters.Domain.UnitTests;

public class SkillActionCatalogTests
{
    [Fact]
    public void Rewards_HasEntryForEveryAction()
    {
        foreach (var action in Enum.GetValues<SkillAction>())
        {
            Assert.True(SkillActionCatalog.Rewards.ContainsKey(action), $"Missing catalog entry for {action}");
        }
    }

    [Fact]
    public void Rewards_ForgedIngot_RewardsBothBlacksmithingAndMining()
    {
        var rewards = SkillActionCatalog.Rewards[SkillAction.ForgedIngot];

        Assert.Contains(rewards, r => r.Skill == SkillType.Blacksmithing && r.XpReward == 40);
        Assert.Contains(rewards, r => r.Skill == SkillType.Mining && r.XpReward == 5);
    }

    [Fact]
    public void Rewards_MinedOreDeposit_RewardsOnlyMining()
    {
        var rewards = SkillActionCatalog.Rewards[SkillAction.MinedOreDeposit];

        Assert.Single(rewards);
        Assert.Equal(SkillType.Mining, rewards[0].Skill);
        Assert.Equal(25, rewards[0].XpReward);
    }
}
