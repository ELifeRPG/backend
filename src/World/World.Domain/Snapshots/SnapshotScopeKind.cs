namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// What a snapshot batch's <c>scope</c> names — deliberately narrower than
/// <see cref="Items.ParentKind"/> (which also has <c>World</c>): per the design spec, "scope is
/// Character or Container only in this phase" — a server-wide reconcile is a separate, explicitly
/// authorised staff operation that lands with world-structure state (phase 4), not here. Append-only
/// ordinal, crosses the wire as a string parsed at the endpoint, same convention as every other
/// wire-facing enum in this module.
/// </summary>
public enum SnapshotScopeKind
{
    Character = 0,
    Container = 1,
}
