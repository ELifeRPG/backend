using ELifeRPG.Characters.Domain.Skills;

namespace ELifeRPG.Characters.Domain.Events;

public sealed record CharacterSkillsInitialized(CharacterSkillsId Id, CharacterId CharacterId);
