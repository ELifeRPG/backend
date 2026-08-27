namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// What minted this instance. Every instance is born backend-side via an API call — see the design
/// spec's "Item origin" decision — so this is never inferred, only ever set once at creation
/// (<see cref="ItemInstance.Register"/>) and carried immutably afterward.
///
/// Append-only: ordinals are persisted (same rule as <c>SkillType</c>, <c>XpSource</c>, <c>AppKey</c>
/// elsewhere in this repo). Never insert, remove or reorder a member — only add new ones at the end.
/// </summary>
public enum ItemOrigin
{
    /// <summary>Minted by <c>PurchaseListingHandler</c> when a shop sale settles.</summary>
    ShopPurchase = 0,

    /// <summary>Minted by a gathering action, atomically with the skill XP it also grants.</summary>
    Gathered = 1,

    /// <summary>Minted directly by staff tooling, bypassing any economic transaction.</summary>
    AdminGrant = 2,

    /// <summary>Minted to seed a composed item's structure — e.g. a phone's SIM slot.</summary>
    Provisioned = 3,

    /// <summary>
    /// Reserved for a future snapshot-originated instance (phase 2+ engine-discovered world loot).
    /// Not produced by anything in phase 1.
    /// </summary>
    Snapshot = 4,

    /// <summary>
    /// Minted by <c>AcknowledgeSpawnsHandler</c> for a child entity the mod declares in an ack's
    /// <c>children</c> array — a magazine loaded in a granted rifle, a SIM seated in a granted phone.
    /// Still "born backend-side via an API call" (this class's own summary): the ack call itself is
    /// that API call, even though the child entity existed in-engine before the mod reported it.
    /// </summary>
    EngineSpawnedChild = 5,
}
