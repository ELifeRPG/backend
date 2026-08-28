using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shops.Application.Common;
using Marten;
using Marten.Services;

namespace ELifeRPG.Shops.Infrastructure.Common;

public sealed class MartenShopListingParticipant(IShopsStore store) : ITransactionParticipant<IShopListingRepository>
{
    public IShopListingRepository EnlistIn(CrossModuleSessionHandle handle)
    {
        var rawTransaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(rawTransaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenShopListingRepository(session, rawTransaction);
    }
}
