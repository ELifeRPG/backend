using ELifeRPG.Characters.Domain.Skills;

namespace ELifeRPG.Characters.Domain.Events;

public sealed record SkillXpGranted(CharacterSkillsId Id, SkillType Skill, long Amount, long NewTotalXp, XpSource Source, SkillAction? Action);
