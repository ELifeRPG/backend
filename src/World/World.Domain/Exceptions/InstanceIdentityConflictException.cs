using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised when an incoming upsert names an existing instance id but a different <c>ItemId</c> than
/// the stored row — the mod's UUIDv4 collided, or the wrong id was reused. Reported as a conflict
/// rather than silently overwritten; see the design spec's note on the 122-bit-random id requirement.
///
/// Not yet thrown by anything in phase 1 — the snapshot-apply path that raises it lands in a later
/// phase — but declared now so the domain's exception surface for identity conflicts is in place
/// where the design spec puts it.
/// </summary>
public sealed class InstanceIdentityConflictException(ItemInstanceId instanceId)
    : InvalidOperationException($"Item instance '{instanceId}' already exists with a different item id.")
{
    public ItemInstanceId InstanceId { get; } = instanceId;
}
