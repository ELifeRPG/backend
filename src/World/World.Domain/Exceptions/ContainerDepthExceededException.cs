using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised when placing an instance inside a container would nest it deeper than
/// <see cref="ELifeRPG.World.Domain.Items.ItemInstance.MaxContainerDepth"/>. Distinct from
/// <see cref="ContainerCycleException"/> — this is a legitimate, acyclic placement that is simply too
/// deep, not a structural impossibility — and, like the other domain guard exceptions in this
/// namespace, is meant to be caught by an Application handler and mapped onto a result union case
/// rather than propagate as a 500 (unlike a plain <see cref="InvalidOperationException"/>, which this
/// repo's convention reserves for genuine bugs).
/// </summary>
public sealed class ContainerDepthExceededException(ItemInstanceId instanceId, ItemInstanceId containerInstanceId, int attemptedDepth, int maxDepth)
    : InvalidOperationException(
        $"Moving item instance '{instanceId}' into container '{containerInstanceId}' would nest it " +
        $"{attemptedDepth} levels deep, exceeding the maximum container depth of {maxDepth}.")
{
    public ItemInstanceId InstanceId { get; } = instanceId;

    public ItemInstanceId ContainerInstanceId { get; } = containerInstanceId;

    public int AttemptedDepth { get; } = attemptedDepth;

    public int MaxDepth { get; } = maxDepth;
}
