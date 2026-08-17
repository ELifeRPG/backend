namespace ELifeRPG.Banking.Domain.Events;

public sealed record BankAccountTransferredIn(BankAccountId Id, BankAccountId SourceBankAccountId, decimal Amount);
