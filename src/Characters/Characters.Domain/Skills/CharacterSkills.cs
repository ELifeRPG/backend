using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Domain.Skills;

public class CharacterSkills
{
    public CharacterSkillsId Id { get; private set; }

    public CharacterId CharacterId { get; private set; }

    public IReadOnlyDictionary<SkillType, long> TotalXpBySkill { get; private set; } = new Dictionary<SkillType, long>();

    public static CharacterSkills Create(CharacterSkillsInitialized domainEvent)
    {
        var characterSkills = new CharacterSkills();
        characterSkills.Apply(domainEvent);
        return characterSkills;
    }

    public SkillXpGranted GrantXp(SkillType skill, long amount, XpSource source, SkillAction? action)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "XP amount must be non-negative.");
        }

        var currentTotal = TotalXpBySkill.GetValueOrDefault(skill);
        var domainEvent = new SkillXpGranted(Id, skill, amount, currentTotal + amount, source, action);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(CharacterSkillsInitialized domainEvent)
    {
        Id = domainEvent.Id;
        CharacterId = domainEvent.CharacterId;
    }

    public void Apply(SkillXpGranted domainEvent)
    {
        var updated = new Dictionary<SkillType, long>(TotalXpBySkill)
        {
            [domainEvent.Skill] = domainEvent.NewTotalXp,
        };
        TotalXpBySkill = updated;
    }
}
