namespace ELifeRPG.Characters.Domain.Skills;

/// <summary>Module-local id for the CharacterSkills aggregate's own Marten stream — kept
/// distinct from CharacterId so its stream never collides with the Character aggregate's own
/// stream in the same store/tenant (Marten stream identity is (tenant, id) only, not
/// type-qualified). Not in Shared.Kernel: nothing outside Characters references it, same as
/// ShopListingId staying module-local to Shops.</summary>
[StronglyTypedId]
public partial struct CharacterSkillsId;
