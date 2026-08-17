using System.Text.Json.Serialization;
using ELifeRPG.Items.Domain.Events;

namespace ELifeRPG.Items.Domain;

public class Item
{
    [JsonInclude]
    public ItemId Id { get; private set; }

    [JsonInclude]
    public string DisplayName { get; private set; } = string.Empty;

    [JsonInclude]
    public string PrefabClassName { get; private set; } = string.Empty;

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
    }
}
