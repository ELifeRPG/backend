using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed class MartenBankAccountRepositoryFactory(IBankingStore store, ICurrentGameServer currentGameServer) : IBankAccountRepositoryFactory
{
    // Tracking mode is left at SessionOptions' default deliberately — this session is only ever used
    // for `Events.Append`, never for loading-then-`Store`-ing a mutated document, so dirty-tracking
    // vs. lightweight tracking makes no behavioral difference here.
    public IBankAccountRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var options = SessionOptions.ForTransaction(handle.Unwrap(), shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenBankAccountRepository(session);
    }
}
