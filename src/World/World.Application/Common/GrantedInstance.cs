namespace ELifeRPG.World.Application.Common;

/// <summary>
/// One freshly minted row from a grant — the shape a shop purchase (task 6), a gathering action
/// (task 7) and this module's own <c>GrantItemsCommand</c> all return, so the mod's adopt-and-ack
/// path is written once and reused everywhere an instance is granted.
/// </summary>
public sealed record GrantedInstance(ItemInstanceId InstanceId, ItemId ItemId, string PrefabClassName);
