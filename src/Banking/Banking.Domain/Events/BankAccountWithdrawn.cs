namespace ELifeRPG.Banking.Domain.Events;

public sealed record BankAccountWithdrawn(BankAccountId Id, decimal Amount, decimal Fee, CharacterId CharacterId);
