using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.World.Infrastructure.Common;

public sealed class MartenItemInstanceParticipant(IWorldStore store, TimeProvider timeProvider, IItemCatalogResolver catalogResolver)
    : ITransactionParticipant<IItemInstanceRepository>
{
    public IItemInstanceRepository EnlistIn(CrossModuleSessionHandle handle)
    {
        var transaction = handle.Unwrap();
        var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: false);

        var session = store.OpenSession(options);
        return new MartenItemInstanceRepository(session, timeProvider, catalogResolver);
    }
}
