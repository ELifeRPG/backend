namespace ELifeRPG.Characters.Domain.Skills;

/// <summary>Append only — ordinals are persisted in Marten event/document payloads (no JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.</summary>
public enum SkillAction
{
    MinedOreDeposit,
    ChoppedTree,
    CaughtFish,
    HarvestedCrop,
    ScavengedSalvage,
    ForgedIngot,
    BuiltCarpentryItem,
    CookedMeal,
    TailoredGarment,
    EngineeredComponent,
}
