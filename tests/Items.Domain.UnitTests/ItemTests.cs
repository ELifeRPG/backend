using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Items.Domain.UnitTests;

public class ItemTests
{
    [Fact]
    public void Create_FromItemCreated_SetsAllFields()
    {
        var itemId = new ItemId(Guid.NewGuid());
        var domainEvent = new ItemCreated(itemId, "9mm Ammo Box", "Ammo_9x19_Box", ItemPersistence.Despawns);

        var item = Item.Create(domainEvent);

        Assert.Equal(itemId, item.Id);
        Assert.Equal("9mm Ammo Box", item.DisplayName);
        Assert.Equal("Ammo_9x19_Box", item.PrefabClassName);
        Assert.Equal(ItemPersistence.Despawns, item.Persistence);
    }

    [Fact]
    public void Apply_ReplayingItemCreated_ResultsInSameItem()
    {
        var itemId = new ItemId(Guid.NewGuid());
        var item = new Item();

        item.Apply(new ItemCreated(itemId, "Bandage", "Medical_Bandage", ItemPersistence.Despawns));

        Assert.Equal(itemId, item.Id);
        Assert.Equal("Bandage", item.DisplayName);
        Assert.Equal("Medical_Bandage", item.PrefabClassName);
    }

    // Persistence was added to ItemCreated on 2026-08-26. System.Text.Json binds a missing
    // constructor argument to its default rather than throwing, so anything that genuinely replays a
    // pre-migration stream sees Persistence = 0. The enum is ordered so that 0 is the safe answer:
    // a ground item that despawns, never a vehicle that lives forever.
    [Fact]
    public void Apply_ItemCreatedFromBeforeTheFieldExisted_DefaultsToDespawning()
    {
        var item = new Item();

        item.Apply(new ItemCreated(new ItemId(Guid.NewGuid()), "Legacy", "Legacy_Prefab", default));

        Assert.Equal(ItemPersistence.Despawns, item.Persistence);
    }

    [Fact]
    public void Apply_ItemCreatedForAPersistentItem_DoesNotDespawn()
    {
        var item = new Item();

        item.Apply(new ItemCreated(new ItemId(Guid.NewGuid()), "Pickup Truck", "Vehicle_Pickup", ItemPersistence.Persistent));

        Assert.Equal(ItemPersistence.Persistent, item.Persistence);
    }
}
