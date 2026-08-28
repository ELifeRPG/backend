namespace ELifeRPG.Characters.Domain.Skills;

/// <summary>Append only — ordinals are persisted in Marten event/document payloads (no JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.</summary>
public enum SkillAction
{
    MinedOreDeposit = 0,
    ChoppedTree = 1,
    CaughtFish = 2,
    HarvestedCrop = 3,
    ScavengedSalvage = 4,
    ForgedIngot = 5,
    BuiltCarpentryItem = 6,
    CookedMeal = 7,
    TailoredGarment = 8,
    EngineeredComponent = 9,
}
