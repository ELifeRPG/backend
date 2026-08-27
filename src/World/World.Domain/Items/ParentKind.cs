namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// Discriminates which of an <see cref="ItemInstance"/>'s parent-shaped fields are meaningful. Not a
/// C# <c>union</c> — those are reserved for in-memory Mediator results in this repo (see
/// ARCHITECTURE.md §9e) — so the persisted shape is this enum plus nullable fields on the document,
/// with <see cref="ItemParent"/> as the read-side helper that assembles a coherent view over them.
/// </summary>
public enum ParentKind
{
    /// <summary>Carried directly by a character — no container in between.</summary>
    Character = 0,

    /// <summary>Held inside another <see cref="ItemInstance"/> (a backpack, a phone's SIM slot, …).</summary>
    Container = 1,

    /// <summary>Sitting in the game world — dropped, spawned loot, or a placed deployable.</summary>
    World = 2,
}
