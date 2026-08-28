namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// Why one instance in a snapshot batch was rejected. Per-instance rejections never fail the whole
/// batch — see <c>ApplySnapshotResult.Applied</c> — they are reported so the Bridge can log without
/// retrying. Append-only, same rule as every other wire-facing enum in this module: never insert,
/// remove or reorder a member.
///
/// The first eight were declared whole by task 1 so the wire taxonomy would be complete from the
/// start; task 1 itself only ever produced <see cref="AttributeLimit"/>, <see cref="CycleDetected"/>,
/// <see cref="UnknownItem"/> and <see cref="NotOnThisServer"/>, and task 2's load-and-diff added the
/// other four (<see cref="UnknownInstance"/>, <see cref="StaleRevision"/>,
/// <see cref="IdentityConflict"/>, <see cref="RemovedByStaff"/>) without needing a new value —
/// exactly as intended.
///
/// <see cref="ValueOutOfRange"/> is the one genuinely new value, appended by task 2. None of the
/// original eight describes "the scalar itself is nonsense" (a negative <c>revision</c>, a
/// <c>durability</c> outside its 0..1 fraction, a negative <c>ammo</c>): <see cref="AttributeLimit"/>
/// is about the freeform attribute bag's size, not a typed field's range, and
/// <see cref="IdentityConflict"/>/<see cref="StaleRevision"/> both describe a coherent value that
/// merely disagrees with stored state. Reusing either would have told the Bridge to look for a
/// conflict that isn't there.
///
/// <b>Append-only is not a style preference here — it is a data-corruption guard, and task 3 is the
/// first place that actually bites.</b> Marten stores this enum ordinally inside every
/// <c>ItemInstance.Attributes</c>-adjacent field that carries it, and — more sharply — inside every
/// <c>Domain.Snapshots.AppliedBatchRejection</c> nested in a stored <c>AppliedBatch</c> record. Inserting
/// a member anywhere but the end silently reassigns every ordinal after it: a stored
/// <c>AppliedBatch</c> written before the change and replayed after it (well within its 24-hour
/// retention window, across an ordinary deploy) would deserialize with a <i>different</i> rejection
/// reason than the one it actually recorded — the reviewer reads the wrong reason, and the Bridge logs
/// the wrong one too. Every other consumer of this enum re-derives its value fresh on every request, so
/// this document is the one place a stale ordinal can outlive the code that wrote it.
/// </summary>
public enum SnapshotRejectionReason
{
    UnknownItem = 0,
    UnknownInstance = 1,
    StaleRevision = 2,
    IdentityConflict = 3,
    CycleDetected = 4,
    AttributeLimit = 5,
    NotOnThisServer = 6,
    RemovedByStaff = 7,

    /// <summary>
    /// A typed scalar on the entry is outside any range the field can mean: a negative
    /// <c>revision</c> (the LWW key is monotonic and starts at 0), a <c>durability</c> that isn't a
    /// 0..1 fraction (NaN included), or a negative <c>ammo</c>. Checked purely in memory, before the
    /// load round trip, so a batch carrying one never reaches Postgres.
    /// </summary>
    ValueOutOfRange = 8,
}
