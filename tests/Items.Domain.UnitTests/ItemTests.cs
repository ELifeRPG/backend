using ELifeRPG.Items.Domain;
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
        var domainEvent = new ItemCreated(itemId, "9mm Ammo Box", "Ammo_9x19_Box");

        var item = Item.Create(domainEvent);

        Assert.Equal(itemId, item.Id);
        Assert.Equal("9mm Ammo Box", item.DisplayName);
        Assert.Equal("Ammo_9x19_Box", item.PrefabClassName);
    }

    [Fact]
    public void Apply_ReplayingItemCreated_ResultsInSameItem()
    {
        var itemId = new ItemId(Guid.NewGuid());
        var item = new Item();

        item.Apply(new ItemCreated(itemId, "Bandage", "Medical_Bandage"));

        Assert.Equal(itemId, item.Id);
        Assert.Equal("Bandage", item.DisplayName);
        Assert.Equal("Medical_Bandage", item.PrefabClassName);
    }
}
