namespace ELifeRPG.Shared.Kernel;

/// <summary>
/// Identifies one concrete, persisted thing in the world — as opposed to <see cref="ItemId"/>, which
/// identifies a catalog entry (a kind of thing). Owned by the World module; lives here so Shops can
/// return granted instance ids and Phone can bind a device to one, without either depending on
/// World.Domain — same reasoning as <see cref="ItemId"/>.
///
/// The backend is the sole minter: Reforger has no item stacking, so nothing splits and every
/// instance comes into existence through an API call — a shop purchase, a gathering action, a staff
/// grant, provisioning. The mod adopts the id it is handed, seeding it into the entity's persistence
/// component before registering the entity, and never invents one. An id the backend did not issue
/// is therefore always rejected. See docs/bridge.md.
/// </summary>
[StronglyTypedId]
public partial struct ItemInstanceId;
