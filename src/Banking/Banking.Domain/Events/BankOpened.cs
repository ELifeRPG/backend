namespace ELifeRPG.Banking.Domain.Events;

public sealed record BankOpened(BankId Id, string Name, decimal TransactionFeeBase, decimal TransactionFeeMultiplier);
