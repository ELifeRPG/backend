namespace ELifeRPG.Banking.Domain.Events;

/// <summary>
/// Exactly one of OwnerCharacterId/OwnerCompanyId is set, matching Type — enforced by the two
/// factory paths in Banking.Application (OpenBankAccountCommand for Personal,
/// OpenCorporateBankAccountCommand for Corporate), not by this record itself.
/// </summary>
public sealed record BankAccountOpened(
    BankAccountId Id,
    BankId BankId,
    BankAccountType Type,
    CharacterId? OwnerCharacterId,
    CompanyId? OwnerCompanyId,
    string Number,
    decimal TransactionFeeBase,
    decimal TransactionFeeMultiplier);
