using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed class MartenBankAccountRepositoryFactory(IBankingStore store, ICurrentGameServer currentGameServer) : IBankAccountRepositoryFactory
{
    public IBankAccountRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenBankAccountRepository(session, transaction, currentGameServer.ClientId);
    }
}
