using System.Text.Json.Serialization;
using ELifeRPG.Items.Domain.Events;

namespace ELifeRPG.Items.Domain;

public class Item
{
    [JsonInclude]
    public ItemId Id { get; private set; }

    [JsonInclude]
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// The ArmA Reforger prefab the gameserver spawns. Unique across the catalog — the World module
    /// treats prefab -> ItemId as a function, so two entries claiming one prefab would make an
    /// item's identity ambiguous. Enforced by a Marten unique index and guarded in CreateItemHandler.
    /// </summary>
    [JsonInclude]
    public string PrefabClassName { get; private set; } = string.Empty;

    [JsonInclude]
    public ItemPersistence Persistence { get; private set; }

    public static Item Create(ItemCreated domainEvent)
    {
        var item = new Item();
        item.Apply(domainEvent);
        return item;
    }

    public void Apply(ItemCreated domainEvent)
    {
        Id = domainEvent.Id;
        DisplayName = domainEvent.DisplayName;
        PrefabClassName = domainEvent.PrefabClassName;
        Persistence = domainEvent.Persistence;
    }
}
