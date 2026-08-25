using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Characters.Domain.Skills;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Characters.Domain.UnitTests;

public class CharacterSkillsTests
{
    [Fact]
    public void Create_SetsIdAndCharacterIdFromEvent_WithEmptyXpTable()
    {
        var characterSkillsId = new CharacterSkillsId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());

        var characterSkills = CharacterSkills.Create(new CharacterSkillsInitialized(characterSkillsId, characterId));

        Assert.Equal(characterSkillsId, characterSkills.Id);
        Assert.Equal(characterId, characterSkills.CharacterId);
        Assert.Empty(characterSkills.TotalXpBySkill);
    }

    [Fact]
    public void GrantXp_FirstGrantForSkill_SetsTotalToAmount()
    {
        var characterSkills = CharacterSkills.Create(new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), new CharacterId(Guid.NewGuid())));

        var domainEvent = characterSkills.GrantXp(SkillType.Mining, 25, XpSource.Action, SkillAction.MinedOreDeposit);

        Assert.Equal(25, domainEvent.NewTotalXp);
        Assert.Equal(25, characterSkills.TotalXpBySkill[SkillType.Mining]);
    }

    [Fact]
    public void GrantXp_SecondGrantForSameSkill_AccumulatesOnTopOfPrevious()
    {
        var characterSkills = CharacterSkills.Create(new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), new CharacterId(Guid.NewGuid())));
        characterSkills.GrantXp(SkillType.Mining, 25, XpSource.Action, SkillAction.MinedOreDeposit);

        var domainEvent = characterSkills.GrantXp(SkillType.Mining, 10, XpSource.Action, SkillAction.MinedOreDeposit);

        Assert.Equal(35, domainEvent.NewTotalXp);
        Assert.Equal(35, characterSkills.TotalXpBySkill[SkillType.Mining]);
    }

    [Fact]
    public void GrantXp_DifferentSkills_TrackedIndependently()
    {
        var characterSkills = CharacterSkills.Create(new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), new CharacterId(Guid.NewGuid())));

        characterSkills.GrantXp(SkillType.Mining, 25, XpSource.Action, SkillAction.MinedOreDeposit);
        characterSkills.GrantXp(SkillType.Blacksmithing, 40, XpSource.Action, SkillAction.ForgedIngot);

        Assert.Equal(25, characterSkills.TotalXpBySkill[SkillType.Mining]);
        Assert.Equal(40, characterSkills.TotalXpBySkill[SkillType.Blacksmithing]);
    }

    [Fact]
    public void GrantXp_NegativeAmount_ThrowsArgumentOutOfRangeException()
    {
        var characterSkills = CharacterSkills.Create(new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), new CharacterId(Guid.NewGuid())));

        Assert.Throws<ArgumentOutOfRangeException>(() => characterSkills.GrantXp(SkillType.Mining, -1, XpSource.Action, SkillAction.MinedOreDeposit));
    }

    [Fact]
    public void Apply_ReplayingInitializedThenGranted_ResultsInSameStateAsLive()
    {
        var characterSkillsId = new CharacterSkillsId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());
        var initialized = new CharacterSkillsInitialized(characterSkillsId, characterId);
        var granted = new SkillXpGranted(characterSkillsId, SkillType.Mining, 25, 25, XpSource.Action, SkillAction.MinedOreDeposit);

        var replayed = new CharacterSkills();
        replayed.Apply(initialized);
        replayed.Apply(granted);

        Assert.Equal(characterSkillsId, replayed.Id);
        Assert.Equal(characterId, replayed.CharacterId);
        Assert.Equal(25, replayed.TotalXpBySkill[SkillType.Mining]);
    }
}
