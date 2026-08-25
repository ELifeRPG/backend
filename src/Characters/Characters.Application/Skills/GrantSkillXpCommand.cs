using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Application.Skills;

public union GrantSkillXpResult(GrantSkillXpResult.Granted, GrantSkillXpResult.CharacterNotFound, GrantSkillXpResult.UnknownSkill)
{
    public record Granted(long NewTotalXp, int NewLevel);

    public record CharacterNotFound;

    public record UnknownSkill;
}

public sealed record GrantSkillXpCommand(CharacterId CharacterId, string Skill, long Amount) : IRequest<GrantSkillXpResult>;

public sealed class GrantSkillXpHandler(ICharacterRepository characterRepository, ICharacterSkillsRepository characterSkillsRepository)
    : IRequestHandler<GrantSkillXpCommand, GrantSkillXpResult>
{
    public async ValueTask<GrantSkillXpResult> Handle(GrantSkillXpCommand request, CancellationToken cancellationToken)
    {
        var character = await characterRepository.FindByIdAsync(request.CharacterId, cancellationToken);
        if (character is null)
        {
            return new GrantSkillXpResult.CharacterNotFound();
        }

        if (!Enum.TryParse<SkillType>(request.Skill, out var skill) || !Enum.IsDefined(skill))
        {
            return new GrantSkillXpResult.UnknownSkill();
        }

        var characterSkills = await characterSkillsRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);
        if (characterSkills is null)
        {
            var initialized = new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), request.CharacterId);
            characterSkills = CharacterSkills.Create(initialized);
            characterSkillsRepository.StartStream(characterSkills, initialized);
        }

        var domainEvent = characterSkills.GrantXp(skill, request.Amount, XpSource.ManualGrant, action: null);
        characterSkillsRepository.Append(characterSkills.Id, domainEvent);
        await characterSkillsRepository.SaveChangesAsync(cancellationToken);

        return new GrantSkillXpResult.Granted(domainEvent.NewTotalXp, SkillLeveling.LevelForTotalXp(domainEvent.NewTotalXp));
    }
}
