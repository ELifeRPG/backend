namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// One event shape for both origins. <c>BohemiaId</c> is null for the ordinary portal-first case —
/// a web signup that has not joined the gameserver yet — and is bound later by
/// <see cref="BohemiaIdBound"/> when the player redeems their in-game PIN.
/// </summary>
public sealed record AccountCreated(AccountId Id, GameId? BohemiaId, KeycloakUserId KeycloakUserId);
