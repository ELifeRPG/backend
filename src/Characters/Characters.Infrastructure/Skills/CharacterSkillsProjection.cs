using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Characters.Domain.Skills;
using Marten.Events.Aggregation;

namespace ELifeRPG.Characters.Infrastructure.Skills;

public sealed partial class CharacterSkillsProjection : SingleStreamProjection<CharacterSkills, CharacterSkillsId>
{
    public static CharacterSkills Create(CharacterSkillsInitialized domainEvent) => CharacterSkills.Create(domainEvent);

    public void Apply(CharacterSkills characterSkills, SkillXpGranted domainEvent) => characterSkills.Apply(domainEvent);
}
