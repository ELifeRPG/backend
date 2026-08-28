namespace ELifeRPG.Shops.Domain;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
/// Persisted on the <c>ShopOpened</c> event and on <c>Shop.OwnerType</c>; it also crosses the wire,
/// but as a string parsed in <c>ShopEndpoints</c>, so only the stored form depends on the ordinal.
/// </summary>
public enum ShopOwnerType
{
    Personal = 0,
    Corporate = 1,
}
