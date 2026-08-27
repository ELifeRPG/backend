using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// Read-side view over an <see cref="ItemInstance"/>'s four parent-shaped fields
/// (<see cref="ItemInstance.OwnerCharacterId"/>, <see cref="ItemInstance.ContainerInstanceId"/>,
/// <see cref="ItemInstance.Slot"/>, <see cref="ItemInstance.Transform"/>), exposing the union-ish
/// helpers a caller actually wants instead of juggling four independently-nullable properties.
///
/// Never persisted — the stored shape is <see cref="ParentKind"/> plus those nullable fields on the
/// document itself, per the design spec's "do not persist a C# union" ruling. This type only ever
/// exists in memory, assembled on read via <see cref="ItemInstance.Parent"/>.
/// </summary>
public sealed class ItemParent
{
    public ParentKind Kind { get; }

    public CharacterId? CharacterId { get; }

    public ItemInstanceId? ContainerInstanceId { get; }

    public string? Slot { get; }

    public WorldTransform? Transform { get; }

    internal ItemParent(ParentKind kind, CharacterId? characterId, ItemInstanceId? containerInstanceId, string? slot, WorldTransform? transform)
    {
        Kind = kind;
        CharacterId = characterId;
        ContainerInstanceId = containerInstanceId;
        Slot = slot;
        Transform = transform;
    }

    public bool IsCharacter => Kind == ParentKind.Character;

    public bool IsContainer => Kind == ParentKind.Container;

    public bool IsWorld => Kind == ParentKind.World;
}
