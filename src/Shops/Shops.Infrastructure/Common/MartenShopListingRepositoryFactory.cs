using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shops.Application.Common;
using Marten;
using Marten.Services;

namespace ELifeRPG.Shops.Infrastructure.Common;

public sealed class MartenShopListingRepositoryFactory(IShopsStore store) : IShopListingRepositoryFactory
{
    // Tracking mode is left at SessionOptions' default deliberately — unlike its Companies/Banking
    // counterparts (which only ever call Events.Append), this session's ReserveStockAsync does call
    // LoadAsync then mutate the aggregate in memory, but it never calls Store — only LoadAsync for
    // reading and Events.Append for writing — so dirty-tracking vs. lightweight tracking still makes
    // no behavioral difference here.
    public IShopListingRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var rawTransaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(rawTransaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenShopListingRepository(session, rawTransaction);
    }
}
