using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Application.Common;

public interface ICharacterRepository
{
    ValueTask<Character?> FindByIdAsync(CharacterId characterId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Character>> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    void StartStream(Character character, CharacterCreated domainEvent);

    /// <summary>
    /// Appends an event to an already-created character's stream. See IBankAccountRepository.Append
    /// for the same pattern — one repository instance owns one Marten session for its whole
    /// lifetime, so this and SaveChangesAsync commit together.
    /// </summary>
    void Append<TEvent>(CharacterId characterId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
