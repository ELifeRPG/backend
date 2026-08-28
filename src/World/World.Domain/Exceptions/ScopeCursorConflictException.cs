namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised by <c>MartenItemInstanceRepository.SaveChangesAsync</c> when Marten's optimistic concurrency
/// check on <c>ScopeCursor</c> (the only document type in this module with it enabled — see
/// <c>World.Infrastructure/ServiceCollectionExtensions.cs</c>) rejects the batch's own cursor advance:
/// two <c>Full</c> snapshot batches raced the same scope, and this one's commit lost.
///
/// Unlike every other domain guard exception in this namespace, the caller did nothing wrong — the
/// batch itself was entirely valid, it simply lost a race to an equally valid one. That is what makes
/// <c>ApplySnapshotHandler</c> map this to the one <b>retryable</b> outcome
/// <c>POST /api/inventory/snapshots</c> has (<c>ApplySnapshotResult.ConcurrentReconcile</c>, 409) rather
/// than every other batch-level rejection in this module, all of which are non-retryable by
/// construction. Fix round 1, item 7.
/// </summary>
public sealed class ScopeCursorConflictException()
    : InvalidOperationException("Another Full reconcile for this scope committed first; the caller should retry unmodified.");
