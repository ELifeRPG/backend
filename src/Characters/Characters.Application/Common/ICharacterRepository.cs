using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Application.Common;

public interface ICharacterRepository
{
    ValueTask<Character?> FindByIdAsync(CharacterId characterId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Character>> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves many characters in one round trip. Read-only: used by CharactersOnServerQuery on the
    /// World module's snapshot write path, where a per-character lookup would not be affordable.
    /// </summary>
    ValueTask<IReadOnlyList<Character>> FindByIdsAsync(IReadOnlyList<CharacterId> characterIds, CancellationToken cancellationToken);

    void StartStream(Character character, CharacterCreated domainEvent);

    /// <summary>
    /// Appends an event to an already-created character's stream. See IBankAccountRepository.Append
    /// for the same pattern — one repository instance owns one Marten session for its whole
    /// lifetime, so this and SaveChangesAsync commit together.
    /// </summary>
    void Append<TEvent>(CharacterId characterId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
