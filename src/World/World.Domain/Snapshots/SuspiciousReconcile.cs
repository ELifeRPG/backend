namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// The staff record of a <see cref="SnapshotMode.Full"/> reconcile that was <b>refused</b> because it
/// would have wiped a large inventory while carrying almost nothing — task 4's empty-payload guard.
///
/// <b>Why this document exists at all.</b> This design has no leases: nothing stops a gameserver that
/// booted with a failed mod load, or one caught mid-split, from honestly reporting an empty world for a
/// scope it cannot actually see. A <c>Full</c> batch's whole meaning is "this is everything in this
/// scope", so believing that server costs the player their entire inventory in one commit. Soft delete
/// plus <c>WorldSettings.BatchIdRetentionSeconds</c>' retention window is the <i>only</i> undo this
/// design has, and an undo nobody knows to perform is not an undo — so the refusal has to leave
/// something behind that a human can find. That is this row: the batch is rejected
/// (<c>422 suspicious_reconcile</c>, not retryable), storage is left exactly as it was, and this record
/// is the one thing the transaction does write.
///
/// <b>Keyed on the composite <see cref="BuildKey"/>, not a fresh id per occurrence</b> — the same
/// <c>{gameServerId}:{batchId}</c> shape <see cref="AppliedBatch"/> uses, for both of that type's
/// reasons. A Bridge that resends the same rejected batch (its buffer has no way to know the rejection
/// was terminal until it reads <c>retryable: false</c>) overwrites its own row rather than filling the
/// staff queue with duplicates of one event; and two gameservers that happen to choose the same
/// client-side <c>batchId</c> can never collide on one row, so neither can overwrite — or read — the
/// other's record.
///
/// A plain Marten document, never a projection (global constraint 1, and <c>WorldStoreTests</c>'
/// guard test covers it alongside <see cref="Items.ItemInstance"/>, <see cref="AppliedBatch"/> and
/// <see cref="ScopeCursor"/>): a refusal is a fact recorded once, not an aggregate with history worth
/// replaying.
///
/// Every field is a number or an id the reviewer needs to reconstruct the decision without re-running
/// it — <see cref="ScopeRowCount"/> against <see cref="ScopeRowsThreshold"/>,
/// <see cref="UpsertCount"/> against <see cref="UpsertsThreshold"/>, and
/// <see cref="WouldHaveSwept"/> as a share of <see cref="ScopeRowCount"/> against
/// <see cref="SweptPercentThreshold"/> are the exact comparison that tripped, thresholds included,
/// because all three live in <c>WorldSettings</c> and may well have been retuned by the time anyone
/// reads this row. Which arm fired is readable straight off those numbers, so it is not stored
/// separately as a value that could drift out of agreement with them.
/// </summary>
public sealed class SuspiciousReconcile
{
    /// <summary><c>{gameServerId}:{batchId}</c> — see <see cref="BuildKey"/>. Never the raw <c>batchId</c> alone.</summary>
    public required string Id { get; init; }

    /// <summary>The refused batch's own wire-facing <c>batchId</c>, verbatim — what the Bridge's logs will name.</summary>
    public required Guid BatchId { get; init; }

    /// <summary>The gameserver that submitted it — folded into <see cref="Id"/>, kept as its own field so a staff query can filter on it.</summary>
    public required GameServerId GameServerId { get; init; }

    /// <summary>The scope the batch declared — <see cref="SnapshotScopeKind.Character"/> or <see cref="SnapshotScopeKind.Container"/>; a server-wide <c>Full</c> is refused before it can ever reach here.</summary>
    public required SnapshotScopeKind ScopeKind { get; init; }

    public CharacterId? ScopeCharacterId { get; init; }

    public ItemInstanceId? ScopeContainerInstanceId { get; init; }

    /// <summary>The batch's <c>sequence</c>. Recorded but deliberately <b>not</b> applied: the <see cref="ScopeCursor"/> is never advanced for a refused batch, so a corrected reconcile at this same sequence is still accepted afterwards.</summary>
    public long? Sequence { get; init; }

    /// <summary>How many live rows the sweep would have soft-deleted had this batch been believed — the numerator of the proportional arm, and the number a reviewer most wants first. Deliberately <b>not</b> what <see cref="ScopeRowsThreshold"/> gates on; see that field.</summary>
    public required int WouldHaveSwept { get; init; }

    /// <summary>
    /// How many rows in the scope were <b>at stake</b> — live, not <c>PendingSpawn</c>, not
    /// <c>RemovedByStaff</c> — which is the exact set the sweep's own rules 1 and 2 leave eligible. This
    /// is the number the gate compared against <see cref="ScopeRowsThreshold"/> and the denominator the
    /// proportional arm divided by, so it is what a reviewer needs to reconstruct the decision.
    ///
    /// Deliberately excludes undelivered grants and staff tombstones even though both are live rows in
    /// the scope (review round 3): the sweep can never touch either, so counting them counted rows that
    /// were never at risk — which refused correct batches from characters holding many undelivered
    /// grants, and permanently broke "logged out naked" for anyone holding 26+ tombstones.
    ///
    /// <b>The name says "ScopeRowCount" and means the eligible subset, and that is a ruling rather than
    /// an oversight</b> (review round 4): eligible rows are a subset of the scope's rows, so the name is
    /// imprecise but never names a different quantity — see
    /// <c>WorldSettings.SuspiciousReconcileScopeRowsThreshold</c> for the full reasoning and for why the
    /// staff-facing <c>422</c> title spells "sweep-eligible" out in full instead of relying on this.
    /// </summary>
    public required int ScopeRowCount { get; init; }

    /// <summary>How many upserts the batch carried — the "near-empty payload" half, checked against <see cref="UpsertsThreshold"/>.</summary>
    public required int UpsertCount { get; init; }

    /// <summary>How many deletes the batch carried. Not part of the guard's condition; recorded because a batch that explicitly deletes what it also fails to report is a different failure mode from one that simply reports nothing.</summary>
    public required int DeleteCount { get; init; }

    /// <summary>
    /// <c>WorldSettings.SuspiciousReconcileScopeRowsThreshold</c> as it stood when this batch was
    /// refused — compared against <see cref="ScopeRowCount"/>, never against
    /// <see cref="WouldHaveSwept"/>; see that setting's own doc comment for why the gate measures what
    /// was at stake rather than what survived the sweep's protection rules, and for why all three of
    /// these names keep "ScopeRows" while meaning the sweep-eligible subset of them (review round 4: a
    /// subset, so imprecise rather than wrong, and cheaper to explain than to rename on a published
    /// contract a second time).
    ///
    /// Deliberately <b>not</b> <c>required</c>, matching <see cref="SweptPercentThreshold"/> below
    /// rather than the fields above it (review round 3). This property arrived after
    /// <see cref="SuspiciousReconcile"/> was already writing rows — it was renamed into existence from
    /// an earlier <c>SweptRowsThreshold</c> — and System.Text.Json throws on a missing <c>required</c>
    /// property rather than leaving it at its default, so marking it <c>required</c> made every
    /// already-stored record unreadable. Unlike <c>AppliedBatch</c> these rows have no retention window
    /// to age out of: a staff record is kept until a human deals with it, so a record written today has
    /// to still deserialize years from now. The rule for this type is therefore stricter than the
    /// general one — <b>only fields present since the first row was written may be <c>required</c></b>,
    /// and anything added afterwards is optional with a meaningful zero.
    /// </summary>
    public int ScopeRowsThreshold { get; init; }

    /// <summary><c>WorldSettings.SuspiciousReconcileUpsertsThreshold</c> as it stood when this batch was refused.</summary>
    public required int UpsertsThreshold { get; init; }

    /// <summary>
    /// <c>WorldSettings.SuspiciousReconcileSweptPercentThreshold</c> as it stood when this batch was
    /// refused — the second, proportional arm of the guard. Not <c>required</c>, unlike its two
    /// neighbours, for the same forward-compatibility reason <see cref="AppliedBatch.Swept"/> is not:
    /// records written before this arm existed must still deserialize, and System.Text.Json throws on a
    /// missing <c>required</c> property rather than defaulting it.
    /// </summary>
    public int SweptPercentThreshold { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Builds this document's composite id — see the class doc comment, and <see cref="AppliedBatch.BuildKey"/> for the same shape's original reasoning.</summary>
    public static string BuildKey(GameServerId gameServerId, Guid batchId) => $"{gameServerId.Value}:{batchId}";
}
