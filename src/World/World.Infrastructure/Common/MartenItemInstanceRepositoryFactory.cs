using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>Mirrors <c>Banking.Infrastructure.Common.MartenBankAccountRepositoryFactory</c> exactly.</summary>
public sealed class MartenItemInstanceRepositoryFactory(IWorldStore store, TimeProvider timeProvider, IItemCatalogResolver catalogResolver)
    : IItemInstanceRepositoryFactory
{
    public IItemInstanceRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenItemInstanceRepository(session, timeProvider, catalogResolver);
    }
}
