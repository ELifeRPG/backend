using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain;
using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Characters.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime (register the repository itself as
/// scoped/transient) rather than injecting a DI-scoped IDocumentSession, since ICharactersStore is
/// a secondary Marten store and only the default store gets an auto-injected scoped session.
/// </summary>
public sealed class MartenCharacterRepository : ICharacterRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenCharacterRepository(ICharactersStore store)
    {
        _session = store.LightweightSession();
    }

    // Marten infers the document id type from Character.Id (CharacterId, not Guid) — pass the
    // strongly-typed id itself here, not .Value. See ARCHITECTURE.md §9e gotcha 4.
    public async ValueTask<Character?> FindByIdAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Character>(characterId, cancellationToken);

    public async ValueTask<IReadOnlyList<Character>> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => await _session.Query<Character>().Where(x => x.AccountId.Value == accountId.Value).ToListAsync(cancellationToken);

    // IsOneOf on the strongly-typed id, never `x.Id.Value`: Marten's LINQ provider rejects the
    // latter outright ("Marten can not (yet) deal with x.Id.Value"). See §9e gotcha 4.
    public async ValueTask<IReadOnlyList<Character>> FindByIdsAsync(IReadOnlyList<CharacterId> characterIds, CancellationToken cancellationToken)
    {
        if (characterIds.Count == 0)
        {
            return [];
        }

        var ids = characterIds.ToArray();
        return await _session.Query<Character>().Where(x => x.Id.IsOneOf(ids)).ToListAsync(cancellationToken);
    }

    public void StartStream(Character character, CharacterCreated domainEvent)
        => _session.Events.StartStream<Character>(character.Id.Value, domainEvent);

    public void Append<TEvent>(CharacterId characterId, TEvent domainEvent) where TEvent : notnull
        => _session.Events.Append(characterId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
