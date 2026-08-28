namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// Why the mod is reporting an instance gone, in one entry of a snapshot batch's <c>deletes</c> array.
/// Purely informational in this task — task 2's diff logic is what actually soft-deletes a row — but
/// declared now so the wire contract's shape is complete and the ordinal is stable from the start.
/// Append-only, same rule as every other wire-facing enum in this module: never insert, remove or
/// reorder a member.
/// </summary>
public enum DeleteReason
{
    Consumed = 0,
    Destroyed = 1,
    Despawned = 2,
    Traded = 3,
    Unknown = 4,
}
