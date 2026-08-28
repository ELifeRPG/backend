using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Application.Common;

/// <summary>
/// The staff record written when task 4's empty-payload guard refuses a <c>Full</c> reconcile — see
/// <see cref="SuspiciousReconcile"/> for why the refusal has to leave something behind.
///
/// Same two-method shape as <see cref="IAppliedBatchRepository"/>, and for the same reason: the write
/// is queued rather than committed, so it joins the batch's own single <c>SaveChangesAsync</c> (global
/// constraint 6) instead of opening a transaction of its own.
/// </summary>
public interface ISuspiciousReconcileRepository
{
    /// <summary>One record by its composite <see cref="SuspiciousReconcile.BuildKey"/> id. No staff read endpoint consumes this yet (deferred with the rest of the staff surface); it exists so the record is addressable by the same key that wrote it.</summary>
    ValueTask<SuspiciousReconcile?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Queues the record. Committed by <see cref="IItemInstanceRepository.SaveChangesAsync"/> on the shared session — never by a second save of its own.</summary>
    void Store(SuspiciousReconcile record);
}
