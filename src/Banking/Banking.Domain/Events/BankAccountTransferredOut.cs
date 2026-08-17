namespace ELifeRPG.Banking.Domain.Events;

public sealed record BankAccountTransferredOut(
    BankAccountId Id,
    BankAccountId TargetBankAccountId,
    decimal Amount,
    decimal Fee,
    CharacterId CharacterId);
