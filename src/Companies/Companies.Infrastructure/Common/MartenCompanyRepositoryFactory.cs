using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Companies.Infrastructure.Common;

public sealed class MartenCompanyRepositoryFactory(ICompaniesStore store, ICurrentGameServer currentGameServer) : ICompanyRepositoryFactory
{
    // Tracking mode is left at SessionOptions' default deliberately — this session is only ever used
    // for `Events.Append`, never for loading-then-`Store`-ing a mutated document, so dirty-tracking
    // vs. lightweight tracking makes no behavioral difference here.
    public ICompanyRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var options = SessionOptions.ForTransaction(handle.Unwrap(), shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenCompanyRepository(session);
    }
}
