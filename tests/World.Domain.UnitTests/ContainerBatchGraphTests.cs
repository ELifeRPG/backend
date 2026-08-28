using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
using Xunit;

namespace ELifeRPG.World.Domain.UnitTests;

/// <summary>
/// The snapshot write path's in-batch cycle/depth guard — see <see cref="ContainerBatchGraph"/>'s own
/// doc comment. Mirrors the shape of <c>ItemInstanceTests</c>' own <c>MoveToContainer</c> cycle/depth
/// tests, but exercised over a plain instance-id graph rather than real, registered instances.
/// </summary>
public sealed class ContainerBatchGraphTests
{
    private static ItemInstanceId NewId() => new(Guid.NewGuid());

    [Fact]
    public void ValidateNoCycleOrExcessiveDepth_ForANonCyclicChainWithinDepth_DoesNotThrow()
    {
        var pouch = NewId();
        var backpack = NewId();
        var instance = NewId();

        // instance -> pouch -> backpack (backpack has no further parent in the batch).
        var edges = new Dictionary<ItemInstanceId, ItemInstanceId> { [pouch] = backpack };

        var exception = Record.Exception(() => ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(instance, pouch, edges));
        Assert.Null(exception);
    }

    [Fact]
    public void ValidateNoCycleOrExcessiveDepth_ForADirectSelfReference_ThrowsContainerCycle()
    {
        var instance = NewId();
        var edges = new Dictionary<ItemInstanceId, ItemInstanceId>();

        var exception = Assert.Throws<ContainerCycleException>(
            () => ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(instance, instance, edges));

        Assert.Equal(instance, exception.InstanceId);
        Assert.Equal(instance, exception.ContainerInstanceId);
    }

    [Fact]
    public void ValidateNoCycleOrExcessiveDepth_ForATransitiveCycle_ThrowsContainerCycle()
    {
        var a = NewId();
        var b = NewId();

        // a's own upsert wants to move into b, but b (already in the batch) is parented into a — a
        // cycle that only closes once both edges exist.
        var edges = new Dictionary<ItemInstanceId, ItemInstanceId> { [b] = a };

        var exception = Assert.Throws<ContainerCycleException>(
            () => ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(a, b, edges));

        Assert.Equal(a, exception.InstanceId);
        Assert.Equal(b, exception.ContainerInstanceId);
    }

    [Fact]
    public void ValidateNoCycleOrExcessiveDepth_BeyondMaxDepth_ThrowsContainerDepthExceeded()
    {
        // chain[0] is the deepest ancestor with no further edge in the batch (an anchor — the same
        // role a Character/World-parented instance plays in ItemInstance.ResolveDepthOrThrow); each
        // further element is parented one deeper than the last, so chain[1..MaxContainerDepth] each
        // carry an edge to the one before. Moving the instance under test into chain[^1] (the direct
        // container) walks exactly MaxContainerDepth+1 steps — one too many.
        var chain = Enumerable.Range(0, ItemInstance.MaxContainerDepth + 1).Select(_ => NewId()).ToList();
        var edges = new Dictionary<ItemInstanceId, ItemInstanceId>();
        for (var i = 1; i < chain.Count; i++)
        {
            edges[chain[i]] = chain[i - 1];
        }

        var instance = NewId();

        var exception = Assert.Throws<ContainerDepthExceededException>(
            () => ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(instance, chain[^1], edges));

        Assert.Equal(instance, exception.InstanceId);
        Assert.Equal(chain[^1], exception.ContainerInstanceId);
        Assert.Equal(ItemInstance.MaxContainerDepth, exception.MaxDepth);
        Assert.Equal(ItemInstance.MaxContainerDepth + 1, exception.AttemptedDepth);
    }

    [Fact]
    public void ValidateNoCycleOrExcessiveDepth_ForAChainThatExitsTheBatch_DoesNotThrowOnThePartialWalk()
    {
        // The direct container isn't named by any upsert in this batch at all — nothing more to walk,
        // so this can't observe a cycle or a depth violation from the batch alone. Task 2's load
        // round trip is what completes this check against the stored chain.
        var instance = NewId();
        var containerNotInBatch = NewId();
        var edges = new Dictionary<ItemInstanceId, ItemInstanceId>();

        var exception = Record.Exception(
            () => ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(instance, containerNotInBatch, edges));
        Assert.Null(exception);
    }
}
