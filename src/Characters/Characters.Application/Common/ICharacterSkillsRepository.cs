using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Characters.Domain.Skills;

namespace ELifeRPG.Characters.Application.Common;

public interface ICharacterSkillsRepository
{
    ValueTask<CharacterSkills?> FindByCharacterIdAsync(CharacterId characterId, CancellationToken cancellationToken);

    void StartStream(CharacterSkills characterSkills, CharacterSkillsInitialized domainEvent);

    /// <summary>
    /// Appends an event to an already-started CharacterSkills stream, addressed by its own
    /// CharacterSkillsId (not CharacterId — the two are deliberately distinct ids so the
    /// CharacterSkills stream never collides with the Character aggregate's own stream in the
    /// same store/tenant).
    /// </summary>
    void Append<TEvent>(CharacterSkillsId characterSkillsId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
