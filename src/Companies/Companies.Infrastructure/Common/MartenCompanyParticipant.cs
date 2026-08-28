using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Companies.Infrastructure.Common;

public sealed class MartenCompanyParticipant(ICompaniesStore store) : ITransactionParticipant<ICompanyRepository>
{
    public ICompanyRepository EnlistIn(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenCompanyRepository(session, transaction);
    }
}
