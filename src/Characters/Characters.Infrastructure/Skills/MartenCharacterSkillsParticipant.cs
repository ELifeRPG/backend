using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Infrastructure.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten.Services;

namespace ELifeRPG.Characters.Infrastructure.Skills;

public sealed class MartenCharacterSkillsParticipant(ICharactersStore store) : ITransactionParticipant<ICharacterSkillsRepository>
{
    public ICharacterSkillsRepository EnlistIn(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenCharacterSkillsRepository(session);
    }
}
