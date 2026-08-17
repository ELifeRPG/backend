namespace ELifeRPG.Banking.Domain.Events;

public sealed record BankAccountDeposited(BankAccountId Id, decimal Amount, decimal Fee);
