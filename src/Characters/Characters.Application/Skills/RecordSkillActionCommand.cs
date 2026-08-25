using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Application.Skills;

public union RecordSkillActionResult(RecordSkillActionResult.Recorded, RecordSkillActionResult.CharacterNotFound, RecordSkillActionResult.UnknownAction)
{
    public record Recorded(IReadOnlyList<SkillXpGrant> Gains, IReadOnlyList<CharacterSkillView> FullState);

    public record CharacterNotFound;

    public record UnknownAction;
}

public sealed record SkillXpGrant(SkillType Skill, long XpGained, long NewTotalXp, int NewLevel, bool DidLevelUp);

public sealed record RecordSkillActionCommand(CharacterId CharacterId, string Action, int Quantity = 1) : IRequest<RecordSkillActionResult>;

public sealed class RecordSkillActionHandler(ICharacterRepository characterRepository, ICharacterSkillsRepository characterSkillsRepository)
    : IRequestHandler<RecordSkillActionCommand, RecordSkillActionResult>
{
    public async ValueTask<RecordSkillActionResult> Handle(RecordSkillActionCommand request, CancellationToken cancellationToken)
    {
        var character = await characterRepository.FindByIdAsync(request.CharacterId, cancellationToken);
        if (character is null)
        {
            return new RecordSkillActionResult.CharacterNotFound();
        }

        if (!Enum.TryParse<SkillAction>(request.Action, out var action) || !SkillActionCatalog.Rewards.TryGetValue(action, out var rewards))
        {
            return new RecordSkillActionResult.UnknownAction();
        }

        var characterSkills = await characterSkillsRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);
        if (characterSkills is null)
        {
            var initialized = new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), request.CharacterId);
            characterSkills = CharacterSkills.Create(initialized);
            characterSkillsRepository.StartStream(characterSkills, initialized);
        }

        var gains = new List<SkillXpGrant>();
        foreach (var reward in rewards)
        {
            var levelBefore = SkillLeveling.LevelForTotalXp(characterSkills.TotalXpBySkill.GetValueOrDefault(reward.Skill));
            var domainEvent = characterSkills.GrantXp(reward.Skill, reward.XpReward * request.Quantity, XpSource.Action, action);
            var levelAfter = SkillLeveling.LevelForTotalXp(domainEvent.NewTotalXp);

            characterSkillsRepository.Append(characterSkills.Id, domainEvent);
            gains.Add(new SkillXpGrant(reward.Skill, domainEvent.Amount, domainEvent.NewTotalXp, levelAfter, levelAfter > levelBefore));
        }

        await characterSkillsRepository.SaveChangesAsync(cancellationToken);

        return new RecordSkillActionResult.Recorded(gains, CharacterSkillViews.BuildFullState(characterSkills.TotalXpBySkill));
    }
}
