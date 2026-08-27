using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised when placing an instance inside a container would make that instance its own ancestor,
/// directly (moving it into itself) or transitively (moving it into something already nested inside
/// it). Loading a character's inventory walks <c>ContainerInstanceId</c> to rebuild the tree; an
/// undetected cycle there is non-terminating.
/// </summary>
public sealed class ContainerCycleException(ItemInstanceId instanceId, ItemInstanceId containerInstanceId)
    : InvalidOperationException(
        $"Item instance '{instanceId}' cannot be placed inside container '{containerInstanceId}': " +
        "it is already an ancestor of that container.")
{
    public ItemInstanceId InstanceId { get; } = instanceId;

    public ItemInstanceId ContainerInstanceId { get; } = containerInstanceId;
}
