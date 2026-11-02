namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// The player's per-platform <b>Game Identity ID</b>, asserted by the Bridge — never accepted as a
/// bearer credential on its own.
///
/// This is <b>not</b> the player's Bohemia account: a Bohemia account is the player's web identity,
/// whereas a Game Identity ID is issued per platform, so one person playing on PC, Xbox and PS5 has
/// three distinct values.
///
/// The game's own API hands this out as a string (<c>BackendApi.GetPlayerIdentityId</c>), which we
/// parse to a <see cref="Guid"/> at the boundary: a value that is not a UUID fails loudly there
/// rather than being carried around as an opaque string nothing can match on.
/// </summary>
[StronglyTypedId]
public partial struct GameIdentityId;
