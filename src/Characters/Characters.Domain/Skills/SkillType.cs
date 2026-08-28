namespace ELifeRPG.Characters.Domain.Skills;

/// <summary>Append only — ordinals are persisted in Marten event/document payloads (no JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.</summary>
public enum SkillType
{
    Mining = 0,
    Woodcutting = 1,
    Fishing = 2,
    Farming = 3,
    Scavenging = 4,
    Blacksmithing = 5,
    Carpentry = 6,
    Cooking = 7,
    Tailoring = 8,
    Engineering = 9,
}
