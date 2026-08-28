using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed class MartenBankAccountParticipant(IBankingStore store) : ITransactionParticipant<IBankAccountRepository>
{
    public IBankAccountRepository EnlistIn(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenBankAccountRepository(session, transaction);
    }
}
