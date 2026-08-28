namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// Discriminates which of an <see cref="ItemInstance"/>'s parent-shaped fields are meaningful. Not a
/// C# <c>union</c> — those are reserved for in-memory Mediator results in this repo (see
/// ARCHITECTURE.md §9e) — so the persisted shape is this enum plus nullable fields on the document,
/// with <see cref="ItemParent"/> as the read-side helper that assembles a coherent view over them.
///
/// Append-only, same rule as <see cref="ItemOrigin"/>: the ordinal is persisted on
/// <c>ItemInstance.ParentKind</c> — patched there directly by <c>MartenItemInstanceRepository</c>'s
/// reparent path — so never insert, remove or reorder a member. The values were already explicit; this
/// note records why they must stay that way.
/// </summary>
public enum ParentKind
{
    /// <summary>Carried directly by a character — no container in between.</summary>
    Character = 0,

    /// <summary>Held inside another <see cref="ItemInstance"/> (a backpack, a rifle's magazine well, …).</summary>
    Container = 1,

    /// <summary>Sitting in the game world — dropped, spawned loot, or a placed deployable.</summary>
    World = 2,
}
