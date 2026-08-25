using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Skills;

public sealed record CharacterSkillView(SkillType Skill, SkillCategory Category, long TotalXp, int Level, long XpForNextLevel);

public static class CharacterSkillViews
{
    public static IReadOnlyList<CharacterSkillView> BuildFullState(IReadOnlyDictionary<SkillType, long> totalXpBySkill)
        => SkillCatalog.Entries.Select(entry =>
        {
            var totalXp = totalXpBySkill.GetValueOrDefault(entry.Key);
            var level = SkillLeveling.LevelForTotalXp(totalXp);
            var xpForNextLevel = level >= SkillLeveling.MaxLevel ? 0 : SkillLeveling.XpForNextLevel(level);
            return new CharacterSkillView(entry.Key, entry.Value.Category, totalXp, level, xpForNextLevel);
        }).ToList();
}

public sealed record CharacterSkillsQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<CharacterSkillView>>;

public sealed class CharacterSkillsQueryHandler(ICharacterSkillsRepository characterSkillsRepository)
    : IRequestHandler<CharacterSkillsQuery, IReadOnlyList<CharacterSkillView>>
{
    public async ValueTask<IReadOnlyList<CharacterSkillView>> Handle(CharacterSkillsQuery request, CancellationToken cancellationToken)
    {
        var characterSkills = await characterSkillsRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);
        var totalXpBySkill = characterSkills?.TotalXpBySkill ?? new Dictionary<SkillType, long>();

        return CharacterSkillViews.BuildFullState(totalXpBySkill);
    }
}
