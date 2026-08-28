using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Application.Common;

/// <summary>
/// Backs task 3's batch-level idempotency — see <c>ApplySnapshotHandler</c>'s own doc comment for
/// exactly where in its validation order the lookup happens and why.
/// </summary>
public interface IAppliedBatchRepository
{
    /// <summary><paramref name="key"/> is <see cref="AppliedBatch.BuildKey"/>'s composite, not the raw <c>batchId</c> alone — see that type's own doc comment for fix round 2, item 3.</summary>
    ValueTask<AppliedBatch?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a genuine insert — this id is never written twice, since a real replay of the same
    /// composite key is caught by <see cref="FindAsync"/> before the handler ever reaches the write
    /// path again. Joins the caller's own <c>IWorldSession</c>; nothing reaches Postgres until the
    /// caller's own <c>SaveChangesAsync</c> runs — see the "one <c>SaveChangesAsync</c> per batch"
    /// constraint on <c>ApplySnapshotHandler</c>.
    /// </summary>
    void Store(AppliedBatch batch);
}
