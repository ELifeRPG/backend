namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// Why a spawn attempt failed — the body of <c>POST /api/inventory/instances/{id}/spawn-failed</c>,
/// per the phase 1 task brief. Lives in the domain (not World.Application, where it started) because
/// <see cref="ItemInstance.RecordSpawnFailure"/> persists it: the reason is what tells staff whether a
/// row on the undeliverable queue is worth redelivering (<see cref="InventoryFull"/>, transient) or
/// needs the mod fixed first and the purchase refunded (<see cref="PrefabMissing"/>,
/// <see cref="AdoptionUnsupported"/>).
///
/// Append-only, same rule as <see cref="ItemOrigin"/>: ordinals travel on the wire as a string today
/// (no <c>JsonStringEnumConverter</c> is configured — see <c>ItemModule</c>'s own note on this) and are
/// persisted on <see cref="ItemInstance"/>, so never insert, remove or reorder a member.
/// </summary>
public enum SpawnFailureReason
{
    /// <summary>The pre-flight check the mod is expected to run (see the design spec) still let this through, or there was none to run (a portal purchase delivered at join).</summary>
    InventoryFull = 0,

    /// <summary>The mod has no prefab registered for the granted item's PrefabClassName.</summary>
    PrefabMissing = 1,

    /// <summary>The instance was meant to spawn into a container that no longer exists.</summary>
    ContainerMissing = 2,

    /// <summary>The mod cannot seed a backend-granted id into this entity type's persistence component at all.</summary>
    AdoptionUnsupported = 3,
}
