namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// Raised when a web identity is attached to an account that had none — the join-first player who
/// signs up (or signs in) afterwards. The inverse direction of
/// <see cref="GameAccountLinked"/>.
/// </summary>
public sealed record KeycloakUserAttached(AccountId Id, KeycloakUserId KeycloakUserId);
