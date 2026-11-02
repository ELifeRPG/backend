namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// One event shape for both origins. <c>KeycloakUserId</c> is null for a <b>join-first</b> account —
/// created because an unknown game identity joined a gameserver, with no web identity yet — and is
/// filled in later by <see cref="KeycloakUserAttached"/>. It is set from the start for the
/// portal-first case.
///
/// Carries no game identity: those live in <see cref="GameAccountLink"/> documents, one per
/// platform, because an account can hold several.
/// </summary>
public sealed record AccountCreated(AccountId Id, KeycloakUserId? KeycloakUserId);
