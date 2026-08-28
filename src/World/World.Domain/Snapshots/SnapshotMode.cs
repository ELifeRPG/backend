namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// How a snapshot batch (<c>POST /api/inventory/snapshots</c>) relates to what the backend already has
/// for its <c>scope</c>. Append-only ordinal, same rule as every other wire-facing enum in this module
/// (<see cref="Items.ItemOrigin"/>, <see cref="Items.SpawnFailureReason"/>) — never insert, remove or
/// reorder a member. Crosses the wire as a string, parsed at the endpoint (no
/// <c>JsonStringEnumConverter</c> is configured anywhere in this solution).
/// </summary>
public enum SnapshotMode
{
    /// <summary>A delta: only the instances named in this batch are considered. No cursor needed — see the design spec's "Why per-scope and not per-server".</summary>
    Partial = 0,

    /// <summary>
    /// "This is everything in this scope." Requires <c>sequence</c>, gated by a per-scope
    /// <c>ScopeCursor</c> (task 3), and — task 4 — soft-deletes whatever the scope's live rows have that
    /// this payload doesn't, excluding <see cref="Items.ItemInstance.PendingSpawn"/> rows (a row the
    /// game has never spawned cannot be missing from a report of what the game can see), staff
    /// tombstones, and any container a surviving row is still nested inside. Its scope must be one
    /// bounded <see cref="SnapshotScopeKind.Character"/> or <see cref="SnapshotScopeKind.Container"/>;
    /// a server-wide reconcile is a separate staff operation, refused here. A sweep that is large while
    /// the payload is near-empty is refused whole and recorded as a <see cref="SuspiciousReconcile"/> —
    /// see <c>ApplySnapshotHandler</c>'s own doc comment for the whole shape.
    /// </summary>
    Full = 1,
}
