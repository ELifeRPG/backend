namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// The player's Bohemia ID, asserted by the Bridge — never accepted as a bearer credential on its own.
/// </summary>
[StronglyTypedId]
public partial struct GameId;
