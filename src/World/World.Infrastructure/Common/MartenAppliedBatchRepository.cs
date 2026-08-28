using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Snapshots;
using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>
/// Joins the shared <see cref="IWorldSession"/> like every other World repository, so its
/// <see cref="Store"/> lands in the same <c>SaveChangesAsync</c> as the batch's <c>ItemInstance</c>
/// writes — see <see cref="WorldSession"/> for why this module shares one unit of work per scope.
/// </summary>
public sealed class MartenAppliedBatchRepository(IWorldSession worldSession) : IAppliedBatchRepository
{
    private readonly IDocumentSession _session = worldSession.Session;

    public async ValueTask<AppliedBatch?> FindAsync(string key, CancellationToken cancellationToken)
        => await _session.LoadAsync<AppliedBatch>(key, cancellationToken);

    public void Store(AppliedBatch batch) => _session.Store(batch);
}
