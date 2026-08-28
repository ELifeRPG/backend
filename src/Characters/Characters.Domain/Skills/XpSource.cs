namespace ELifeRPG.Characters.Domain.Skills;

/// <summary>Append only — ordinals are persisted in Marten event/document payloads (no JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.</summary>
public enum XpSource
{
    Action = 0,
    ManualGrant = 1,
}
