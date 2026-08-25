namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// Raised when a player's in-game Bohemia identity is bound to their existing account. The binding
/// itself is performed in Keycloak (the player types an in-game PIN into Keycloak's own form); this
/// event records the result on the account so the gameserver can resolve it on the next join.
/// </summary>
public sealed record BohemiaIdBound(AccountId Id, GameId BohemiaId);
