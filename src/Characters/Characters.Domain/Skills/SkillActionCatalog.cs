namespace ELifeRPG.Characters.Domain.Skills;

public sealed record SkillActionReward(SkillType Skill, long XpReward);

public static class SkillActionCatalog
{
    public static readonly IReadOnlyDictionary<SkillAction, IReadOnlyList<SkillActionReward>> Rewards = new Dictionary<SkillAction, IReadOnlyList<SkillActionReward>>
    {
        [SkillAction.MinedOreDeposit] = [new SkillActionReward(SkillType.Mining, 25)],
        [SkillAction.ChoppedTree] = [new SkillActionReward(SkillType.Woodcutting, 20)],
        [SkillAction.CaughtFish] = [new SkillActionReward(SkillType.Fishing, 15)],
        [SkillAction.HarvestedCrop] = [new SkillActionReward(SkillType.Farming, 10)],
        [SkillAction.ScavengedSalvage] = [new SkillActionReward(SkillType.Scavenging, 15)],
        [SkillAction.ForgedIngot] = [new SkillActionReward(SkillType.Blacksmithing, 40), new SkillActionReward(SkillType.Mining, 5)],
        [SkillAction.BuiltCarpentryItem] = [new SkillActionReward(SkillType.Carpentry, 35)],
        [SkillAction.CookedMeal] = [new SkillActionReward(SkillType.Cooking, 20)],
        [SkillAction.TailoredGarment] = [new SkillActionReward(SkillType.Tailoring, 30)],
        [SkillAction.EngineeredComponent] = [new SkillActionReward(SkillType.Engineering, 45)],
    };
}
