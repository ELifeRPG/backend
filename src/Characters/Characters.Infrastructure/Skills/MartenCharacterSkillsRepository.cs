using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Characters.Domain.Skills;
using ELifeRPG.Characters.Infrastructure.Common;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Characters.Infrastructure.Skills;

/// <summary>
/// Holds one session for this repository instance's lifetime, same reasoning as
/// MartenCharacterRepository — ICharactersStore is a secondary Marten store, only the default
/// store gets an auto-injected scoped session.
/// </summary>
public sealed class MartenCharacterSkillsRepository : ICharacterSkillsRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenCharacterSkillsRepository(ICharactersStore store)
    {
        _session = store.LightweightSession();
    }

    public async ValueTask<CharacterSkills?> FindByCharacterIdAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<CharacterSkills>().Where(x => x.CharacterId.Value == characterId.Value).FirstOrDefaultAsync(cancellationToken);

    public void StartStream(CharacterSkills characterSkills, CharacterSkillsInitialized domainEvent)
        => _session.Events.StartStream<CharacterSkills>(characterSkills.Id.Value, domainEvent);

    public void Append<TEvent>(CharacterSkillsId characterSkillsId, TEvent domainEvent) where TEvent : notnull
        => _session.Events.Append(characterSkillsId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
