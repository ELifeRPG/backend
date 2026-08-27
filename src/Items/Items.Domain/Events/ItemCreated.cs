namespace ELifeRPG.Items.Domain.Events;

public sealed record ItemCreated(
    ItemId Id,
    string DisplayName,
    string PrefabClassName,
    ItemPersistence Persistence);
