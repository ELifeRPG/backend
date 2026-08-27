namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// Points back at whatever created this instance — a purchase, a gather action, a staff grant. Kept
/// as plain strings rather than another module's strongly-typed id: World must not depend on Shops,
/// Characters or any other module's Domain project to describe where an instance came from (see
/// ARCHITECTURE.md §9e), so the module name and the referenced id both travel as text.
///
/// Immutable after creation, same as <see cref="ItemInstance.Origin"/> and
/// <see cref="ItemInstance.RegisteredAt"/> — this is the only durable provenance an instance has.
/// </summary>
public sealed record OriginRef(string Module, string Id);
