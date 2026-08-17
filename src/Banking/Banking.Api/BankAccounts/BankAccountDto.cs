namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record BankAccountDto
{
    public required Guid BankAccountId { get; init; }

    public required Guid BankId { get; init; }

    public required string Type { get; init; }

    public required Guid? CharacterId { get; init; }

    public required Guid? CompanyId { get; init; }

    public required string Number { get; init; }

    public required decimal Balance { get; init; }

    public static BankAccountDto Create(BankAccount source) => new()
    {
        BankAccountId = source.Id.Value,
        BankId = source.BankId.Value,
        Type = source.Type.ToString(),
        CharacterId = source.OwnerCharacterId?.Value,
        CompanyId = source.OwnerCompanyId?.Value,
        Number = source.Number,
        Balance = source.Balance,
    };

    public static BankAccountDto Create(OpenBankAccountResult.Opened source, Guid bankId, Guid characterId) => new()
    {
        BankAccountId = source.BankAccountId.Value,
        BankId = bankId,
        Type = BankAccountType.Personal.ToString(),
        CharacterId = characterId,
        CompanyId = null,
        Number = source.Number,
        Balance = 0,
    };

    public static BankAccountDto Create(OpenCorporateBankAccountResult.Opened source, Guid bankId, Guid companyId) => new()
    {
        BankAccountId = source.BankAccountId.Value,
        BankId = bankId,
        Type = BankAccountType.Corporate.ToString(),
        CharacterId = null,
        CompanyId = companyId,
        Number = source.Number,
        Balance = 0,
    };
}
