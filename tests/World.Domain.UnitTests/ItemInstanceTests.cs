using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
using Xunit;

namespace ELifeRPG.World.Domain.UnitTests;

public sealed class ItemInstanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan GroundItemTtl = TimeSpan.FromHours(1);

    private static ItemInstance RegisterToCharacter(CharacterId ownerCharacterId)
        => ItemInstance.Register(
            new ItemInstanceId(Guid.NewGuid()),
            new ItemId(Guid.NewGuid()),
            ownerCharacterId,
            ItemOrigin.ShopPurchase,
            new OriginRef("Shops", Guid.NewGuid().ToString()),
            Now);

    private static Func<ItemInstanceId, ItemInstance> LookupIn(params ItemInstance[] instances)
    {
        var byId = instances.ToDictionary(x => x.Id);
        return id => byId[id];
    }

    [Fact]
    public void MoveToContainer_ThatIsItsOwnDescendant_ThrowsContainerCycle()
    {
        var character = new CharacterId(Guid.NewGuid());
        var backpack = RegisterToCharacter(character);
        var pouch = RegisterToCharacter(character);

        // pouch goes inside backpack.
        pouch.MoveToContainer(backpack.Id, "main", LookupIn(backpack, pouch), Now);

        // Now backpack (pouch's ancestor) tries to move into pouch, its own descendant.
        var exception = Assert.Throws<ContainerCycleException>(
            () => backpack.MoveToContainer(pouch.Id, "inner", LookupIn(backpack, pouch), Now));

        Assert.Equal(backpack.Id, exception.InstanceId);
        Assert.Equal(pouch.Id, exception.ContainerInstanceId);
    }

    [Fact]
    public void MoveToContainer_ThatIsItself_ThrowsContainerCycle()
    {
        var character = new CharacterId(Guid.NewGuid());
        var backpack = RegisterToCharacter(character);

        Assert.Throws<ContainerCycleException>(
            () => backpack.MoveToContainer(backpack.Id, null, LookupIn(backpack), Now));
    }

    [Fact]
    public void MoveToContainer_BeyondMaxDepth_Throws()
    {
        var character = new CharacterId(Guid.NewGuid());

        // chain[0] sits directly on the character (container-depth 0, not itself measured). Each
        // further element is nested one deeper than the last via MoveToContainer, so chain[1..6]
        // reach exactly depths 1..MaxContainerDepth — every one of those moves must succeed; it's
        // nesting a seventh level, beyond chain[MaxContainerDepth], that's rejected.
        var chain = new List<ItemInstance> { RegisterToCharacter(character) };
        for (var i = 0; i < ItemInstance.MaxContainerDepth; i++)
        {
            var next = RegisterToCharacter(character);
            next.MoveToContainer(chain[^1].Id, null, LookupIn(chain.ToArray()), Now);
            chain.Add(next);
        }

        Assert.Equal(ItemInstance.MaxContainerDepth + 1, chain.Count);

        var oneTooDeep = RegisterToCharacter(character);
        var lookup = LookupIn([.. chain, oneTooDeep]);

        var exception = Assert.Throws<ContainerDepthExceededException>(
            () => oneTooDeep.MoveToContainer(chain[^1].Id, null, lookup, Now));

        Assert.Equal(oneTooDeep.Id, exception.InstanceId);
        Assert.Equal(chain[^1].Id, exception.ContainerInstanceId);
        Assert.Equal(ItemInstance.MaxContainerDepth, exception.MaxDepth);
        Assert.Equal(ItemInstance.MaxContainerDepth + 1, exception.AttemptedDepth);
    }

    [Fact]
    public void Attributes_ExceedingKeyLimit_ThrowsAttributeLimitExceeded()
    {
        var tooMany = Enumerable.Range(0, ItemAttributes.MaxKeys + 1)
            .ToDictionary(i => $"key{i}", i => $"value{i}");

        Assert.Throws<AttributeLimitExceededException>(() => ItemAttributes.Create(tooMany));
    }

    [Fact]
    public void Attributes_WithAnOverlongKey_ThrowsAttributeLimitExceeded()
    {
        var values = new Dictionary<string, string> { [new string('k', ItemAttributes.MaxKeyLength + 1)] = "value" };

        Assert.Throws<AttributeLimitExceededException>(() => ItemAttributes.Create(values));
    }

    [Fact]
    public void Attributes_WithAnOverlongValue_ThrowsAttributeLimitExceeded()
    {
        var values = new Dictionary<string, string> { ["key"] = new string('v', ItemAttributes.MaxValueLength + 1) };

        Assert.Throws<AttributeLimitExceededException>(() => ItemAttributes.Create(values));
    }

    [Fact]
    public void SetExpiry_ForPersistentItem_LeavesExpiresAtNull()
    {
        var instance = RegisterToCharacter(new CharacterId(Guid.NewGuid()));
        var transform = new WorldTransform(new WorldVector3(1, 2, 3), new WorldVector3(0, 0, 0));

        instance.MoveToWorld(transform, despawns: false, GroundItemTtl, Now);

        Assert.Null(instance.ExpiresAt);
    }

    [Fact]
    public void SetExpiry_ForDespawningItem_ArmsExpiresAt()
    {
        var instance = RegisterToCharacter(new CharacterId(Guid.NewGuid()));
        var transform = new WorldTransform(new WorldVector3(1, 2, 3), new WorldVector3(0, 0, 0));

        instance.MoveToWorld(transform, despawns: true, GroundItemTtl, Now);

        Assert.Equal(Now + GroundItemTtl, instance.ExpiresAt);
    }

    [Fact]
    public void MoveToCharacter_ClearsExpiresAt()
    {
        var character = new CharacterId(Guid.NewGuid());
        var instance = RegisterToCharacter(character);
        var transform = new WorldTransform(new WorldVector3(1, 2, 3), new WorldVector3(0, 0, 0));
        instance.MoveToWorld(transform, despawns: true, GroundItemTtl, Now);
        Assert.NotNull(instance.ExpiresAt);

        instance.MoveToCharacter(character, null, Now);

        Assert.Null(instance.ExpiresAt);
    }

    [Fact]
    public void RootCharacterId_ForNestedContainerChain_ResolvesToTheOwningCharacter()
    {
        var character = new CharacterId(Guid.NewGuid());
        var backpack = RegisterToCharacter(character);
        var pouch = RegisterToCharacter(character);
        var magazine = RegisterToCharacter(character);

        pouch.MoveToContainer(backpack.Id, "main", LookupIn(backpack, pouch, magazine), Now);
        magazine.MoveToContainer(pouch.Id, "mag-slot", LookupIn(backpack, pouch, magazine), Now);

        Assert.Equal(character, pouch.RootCharacterId);
        Assert.Equal(character, magazine.RootCharacterId);
    }

    [Fact]
    public void OriginOriginRefAndRegisteredAt_AfterAnySequenceOfOperations_RemainAsSetAtCreation()
    {
        var character = new CharacterId(Guid.NewGuid());
        var otherCharacter = new CharacterId(Guid.NewGuid());
        var instance = RegisterToCharacter(character);
        var originalOrigin = instance.Origin;
        var originalOriginRef = instance.OriginRef;
        var originalRegisteredAt = instance.RegisteredAt;

        var container = RegisterToCharacter(character);
        var transform = new WorldTransform(new WorldVector3(1, 2, 3), new WorldVector3(0, 0, 0));

        instance.MoveToWorld(transform, despawns: true, GroundItemTtl, Now.AddHours(1));
        instance.MoveToCharacter(otherCharacter, "belt", Now.AddHours(2));
        instance.MoveToContainer(container.Id, "slot", LookupIn(instance, container), Now.AddHours(3));

        Assert.Equal(originalOrigin, instance.Origin);
        Assert.Equal(originalOriginRef, instance.OriginRef);
        Assert.Equal(originalRegisteredAt, instance.RegisteredAt);
    }

    [Fact]
    public void AcknowledgeSpawn_ClearsPendingSpawnAndStampsTheDeliveryServer()
    {
        var character = new CharacterId(Guid.NewGuid());
        var instance = RegisterToCharacter(character);
        var gameServerId = new GameServerId(Guid.NewGuid());
        Assert.True(instance.PendingSpawn);
        Assert.Null(instance.RootGameServerId);

        instance.AcknowledgeSpawn(gameServerId, Now.AddMinutes(1));

        Assert.False(instance.PendingSpawn);
        Assert.Equal(gameServerId, instance.RootGameServerId);
        Assert.Equal(Now.AddMinutes(1), instance.UpdatedAt);
    }

    [Fact]
    public void RegisterChild_ParentsToTheGivenInstance_InheritingItsRootFields()
    {
        var character = new CharacterId(Guid.NewGuid());
        var gameServerId = new GameServerId(Guid.NewGuid());
        var parent = RegisterToCharacter(character);
        parent.AcknowledgeSpawn(gameServerId, Now);
        var childId = new ItemInstanceId(Guid.NewGuid());
        var childItemId = new ItemId(Guid.NewGuid());

        var child = ItemInstance.RegisterChild(childId, childItemId, parent, "mag-1", Now);

        Assert.Equal(childId, child.Id);
        Assert.Equal(childItemId, child.ItemId);
        Assert.Equal(ParentKind.Container, child.ParentKind);
        Assert.Equal(parent.Id, child.ContainerInstanceId);
        Assert.Equal("mag-1", child.Slot);
        Assert.Equal(parent.RootCharacterId, child.RootCharacterId);
        Assert.Equal(parent.RootGameServerId, child.RootGameServerId);
        Assert.False(child.PendingSpawn);
        Assert.Equal(ItemOrigin.EngineSpawnedChild, child.Origin);
    }

    [Fact]
    public void ClearPendingOnExplicitDelete_ClearsPendingSpawn()
    {
        var instance = RegisterToCharacter(new CharacterId(Guid.NewGuid()));
        Assert.True(instance.PendingSpawn);

        instance.ClearPendingOnExplicitDelete(Now.AddMinutes(1));

        Assert.False(instance.PendingSpawn);
        Assert.Equal(Now.AddMinutes(1), instance.UpdatedAt);
    }

    [Fact]
    public void RecordSpawnFailure_StampsReasonTimestampAndIncrementsCount_WithoutTouchingDeliveryAttempts()
    {
        var instance = RegisterToCharacter(new CharacterId(Guid.NewGuid()));
        Assert.Null(instance.LastSpawnFailureReason);
        Assert.Equal(0, instance.SpawnFailureCount);

        instance.RecordSpawnFailure(SpawnFailureReason.InventoryFull, Now.AddMinutes(1));

        Assert.Equal(SpawnFailureReason.InventoryFull, instance.LastSpawnFailureReason);
        Assert.Equal(Now.AddMinutes(1), instance.LastSpawnFailureAt);
        Assert.Equal(1, instance.SpawnFailureCount);
        Assert.Equal(0, instance.DeliveryAttempts);

        instance.RecordSpawnFailure(SpawnFailureReason.PrefabMissing, Now.AddMinutes(2));

        Assert.Equal(SpawnFailureReason.PrefabMissing, instance.LastSpawnFailureReason);
        Assert.Equal(Now.AddMinutes(2), instance.LastSpawnFailureAt);
        Assert.Equal(2, instance.SpawnFailureCount);
    }
}
