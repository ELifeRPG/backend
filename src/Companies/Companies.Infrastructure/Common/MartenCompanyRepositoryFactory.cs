using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Companies.Infrastructure.Common;

public sealed class MartenCompanyRepositoryFactory(ICompaniesStore store, ICurrentGameServer currentGameServer) : ICompanyRepositoryFactory
{
    public ICompanyRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenCompanyRepository(session, transaction, currentGameServer.ClientId);
    }
}
