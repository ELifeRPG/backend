namespace ELifeRPG.Accounts.Domain.Events;

public sealed record AccountCreated(AccountId Id, GameId BohemiaId, KeycloakUserId KeycloakUserId);
