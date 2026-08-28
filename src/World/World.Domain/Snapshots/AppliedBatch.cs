namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// One rejected instance recorded inside an <see cref="AppliedBatch"/>'s stored response — the
/// domain-layer twin of <c>World.Application.Inventory.SnapshotRejection</c>. Duplicated rather than
/// shared because <c>World.Domain</c> has zero dependencies (ARCHITECTURE.md §9e) and that record lives
/// in <c>World.Application</c>; <c>ApplySnapshotHandler</c> maps between the two when it stores and
/// replays a batch.
/// </summary>
public readonly record struct AppliedBatchRejection(ItemInstanceId InstanceId, SnapshotRejectionReason Reason);

/// <summary>
/// Idempotency record for <c>POST /api/inventory/snapshots</c> (task 3), keyed on a composite of the
/// Bridge-supplied <c>batchId</c> <b>and</b> the calling gameserver (<see cref="BuildKey"/>) rather than
/// the raw <c>batchId</c> alone — that is what makes "has this exact batch already been applied" a
/// single point lookup (<c>session.LoadAsync&lt;AppliedBatch&gt;(key)</c>) rather than a query, while
/// also closing both the read and write halves of a cross-tenant leak (fix round 1 item 3, tightened in
/// fix round 2 item 3 — see below).
///
/// One row is written per batch whose outcome is <c>ApplySnapshotResult.Applied</c> — the only outcome
/// this path ever mutates storage for. Every other batch-level rejection (<c>DuplicateInstanceId</c>,
/// <c>BatchTooLarge</c>, <c>WrongServer</c>, <c>StaleSequence</c>, <c>SequenceOutOfRange</c>) never
/// touches storage in the first place, so recording one here would only add a write — and therefore a
/// Postgres touch — to a path whose whole point is that a malformed or not-yet-authorised batch never
/// reaches Postgres for its own sake. Replaying one of those is safe without a stored record: it is a
/// pure function of the request and (for <c>WrongServer</c> and <c>StaleSequence</c>) of state this
/// handler already has to re-read to answer the question again, so recomputing it is exactly as cheap
/// and exactly as correct as looking it up would be. <c>ConcurrentReconcile</c> is the remaining case,
/// and it is never recorded for a different reason: it means the whole transaction — including the
/// <c>AppliedBatch</c> row this write would have queued — rolled back, so there is nothing to record.
///
/// Stores exactly the fields <c>ApplySnapshotResponseDto</c> needs to reconstruct the original response
/// byte-for-byte — only <c>replayOfPriorBatch</c> differs, <c>false</c> the first time and <c>true</c>
/// on every replay — per the design brief's "the stored replay body must be byte-identical to the
/// original response, including the per-instance <c>rejected</c> array and the <c>cascadeDeleted</c>
/// count." <see cref="BatchId"/> carries the batch's own wire-facing id separately from <see cref="Id"/>
/// (the composite key), since that is the value <c>ApplySnapshotResponseDto</c> actually echoes back.
///
/// A plain Marten document, never a projection — same reasoning as <see cref="Items.ItemInstance"/>:
/// this is a last-write-once cache of a response, not an aggregate with history worth replaying.
/// <see cref="AppliedAt"/> is what <c>WorldSettings.BatchIdRetentionSeconds</c> is measured against; a
/// record older than the retention window is treated as though it were never found — safe because
/// per-instance revision last-write-wins already makes re-applying the same content past that window a
/// no-op on its own (belt and braces, per the design brief).
///
/// <b>Fix round 1, item 3 found the read half of a cross-tenant leak: fix round 2 found the write half
/// was still open, and this composite key is what closes both at once.</b> Fix round 1's shape kept
/// <c>Id</c> as the raw <c>batchId</c> and instead compared a separately-stored <see cref="GameServerId"/>
/// on every lookup — which stopped server B from *reading* the body A's batch recorded, but did nothing
/// to stop B from *writing* to the same row: B submitting a batch under A's own <c>batchId</c> would
/// overwrite A's record with B's body (a plain Marten upsert, no concurrency check on this document),
/// after which A's own later replay of its own <c>batchId</c> would miss and silently re-apply. Keying
/// on <see cref="BuildKey"/>'s composite instead makes A's and B's records different rows from the
/// start, so B's write can never touch A's — a legitimate Bridge retry still hits, since it always
/// resends under the same <c>gameServerId</c> and <c>batchId</c> pair it originally used.
/// </summary>
public sealed class AppliedBatch
{
    /// <summary><c>{gameServerId}:{batchId}</c> — see <see cref="BuildKey"/>. Never the raw <c>batchId</c> alone.</summary>
    public required string Id { get; init; }

    /// <summary>The batch's own wire-facing <c>batchId</c>, verbatim — what <c>ApplySnapshotResponseDto</c> echoes back on a replay.</summary>
    public required Guid BatchId { get; init; }

    /// <summary>The calling gameserver that originally applied this batch — folded into <see cref="Id"/>; kept as its own field for readability (logs, ad-hoc queries) rather than only implicitly.</summary>
    public required GameServerId GameServerId { get; init; }

    public required DateTimeOffset AppliedAt { get; init; }

    public long? Sequence { get; init; }

    public required int AppliedCount { get; init; }

    public required int SkippedNoOp { get; init; }

    public required int Deleted { get; init; }

    public required int CascadeDeleted { get; init; }

    /// <summary>
    /// Task 4: how many rows this batch's <see cref="SnapshotMode.Full"/> sweep soft-deleted for being
    /// absent from the payload — rows the request never named, which is why they are neither
    /// <see cref="Deleted"/> (that counts only entries the <c>deletes</c> array asked for) nor
    /// <see cref="CascadeDeleted"/> (that counts descendants of those).
    ///
    /// Deliberately <b>not</b> <c>required</c>, unlike its neighbours, and that is load-bearing rather
    /// than an oversight: <see cref="AppliedBatch"/> rows written before this field existed are still
    /// inside their 24-hour <c>BatchIdRetentionSeconds</c> window across an ordinary deploy, and
    /// System.Text.Json throws on a missing <c>required</c> property rather than leaving it at its
    /// default. Absent reads back as 0, which is the truthful value for a batch applied before the
    /// sweep existed. Same reasoning as <see cref="Sequence"/> next door, and the same reasoning
    /// <c>WorldSettings</c>' class doc records for its own property initializers.
    /// </summary>
    public int Swept { get; init; }

    public required IReadOnlyList<AppliedBatchRejection> Rejected { get; init; }

    /// <summary>Builds this document's composite id — see the class doc comment's fix round 2, item 3.</summary>
    public static string BuildKey(GameServerId gameServerId, Guid batchId) => $"{gameServerId.Value}:{batchId}";
}
