using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// The snapshot write path's cycle/depth guard — see the design spec's write-path mechanics step 1
/// ("build the in-batch parent graph, run the cycle guard ... in memory"). Algorithmically the same
/// walk as <see cref="ItemInstance"/>'s own private <c>ResolveDepthOrThrow</c> (same
/// <see cref="ItemInstance.MaxContainerDepth"/> constant, same two exceptions), but adapted to run
/// over a plain in-batch parent map instead of a real, already-registered <see cref="ItemInstance"/>
/// with a <c>resolveContainer</c> callback: a snapshot batch describes instances the backend has never
/// stored, so <see cref="ItemInstance.MoveToContainer"/> has nothing yet to call it on.
///
/// Deliberately scoped to only the portion of a container chain visible <b>within one batch</b>: the
/// moment the walk reaches a container id the batch itself doesn't also upsert, it stops rather than
/// guessing — that container's own chain (if any) lives in storage, and validating it needs the load
/// round trip task 2 owns (<c>ApplySnapshotCommand</c>'s own doc comment explains why task 1 doesn't
/// do that load). A chain that never leaves the batch is validated completely; one that does gets a
/// partial check now and the rest at task 2's apply time.
/// </summary>
public static class ContainerBatchGraph
{
    /// <summary>
    /// Walks from <paramref name="containerInstanceId"/> through <paramref name="containerParentByInstanceId"/>
    /// (child instance id → its own container-kind parent's instance id, restricted to Container-kind
    /// upserts elsewhere in the same batch), throwing the moment <paramref name="instanceId"/> itself
    /// reappears — which is what "would become its own ancestor" looks like from the target's side,
    /// direct or transitive — or the chain exceeds <see cref="ItemInstance.MaxContainerDepth"/>.
    /// </summary>
    /// <exception cref="ContainerCycleException"/>
    /// <exception cref="ContainerDepthExceededException"/>
    public static void ValidateNoCycleOrExcessiveDepth(
        ItemInstanceId instanceId,
        ItemInstanceId containerInstanceId,
        IReadOnlyDictionary<ItemInstanceId, ItemInstanceId> containerParentByInstanceId)
    {
        var depth = 1;
        var visited = new HashSet<ItemInstanceId> { instanceId };
        var currentId = containerInstanceId;

        while (true)
        {
            if (!visited.Add(currentId))
            {
                throw new ContainerCycleException(instanceId, containerInstanceId);
            }

            if (!containerParentByInstanceId.TryGetValue(currentId, out var nextId))
            {
                // The chain exits the batch here — nothing further to walk without a stored read.
                break;
            }

            depth++;
            currentId = nextId;
        }

        if (depth > ItemInstance.MaxContainerDepth)
        {
            throw new ContainerDepthExceededException(instanceId, containerInstanceId, depth, ItemInstance.MaxContainerDepth);
        }
    }
}
