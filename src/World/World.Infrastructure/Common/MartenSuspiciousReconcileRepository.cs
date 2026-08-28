using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Snapshots;
using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>
/// Copies <see cref="MartenAppliedBatchRepository"/>'s shape verbatim, joining the shared
/// <see cref="IWorldSession"/> so the refusal record commits in the refused batch's own single
/// transaction — which, for a refused batch, is a transaction that writes this row and nothing else.
/// </summary>
public sealed class MartenSuspiciousReconcileRepository(IWorldSession worldSession) : ISuspiciousReconcileRepository
{
    private readonly IDocumentSession _session = worldSession.Session;

    public async ValueTask<SuspiciousReconcile?> FindAsync(string id, CancellationToken cancellationToken)
        => await _session.LoadAsync<SuspiciousReconcile>(id, cancellationToken);

    public void Store(SuspiciousReconcile record) => _session.Store(record);
}
