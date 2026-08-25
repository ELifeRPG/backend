namespace ELifeRPG.Accounts.Domain.Events;

public sealed record WhitelistApplicationSubmitted(WhitelistApplicationId Id, AccountId AccountId, string ApplicationText);
