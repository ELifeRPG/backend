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

    /// <summary>
    /// The fee terms copied from the bank when the account was opened — the ones this account is
    /// actually charged, even after the bank's own terms change. Fee = base + amount × multiplier,
    /// rounded to whole cents as documented on <c>Money</c>.
    /// </summary>
    public required decimal TransactionFeeBase { get; init; }

    public required decimal TransactionFeeMultiplier { get; init; }

    public static BankAccountDto Create(BankAccount source) => new()
    {
        BankAccountId = source.Id.Value,
        BankId = source.BankId.Value,
        Type = source.Type.ToString(),
        CharacterId = source.OwnerCharacterId?.Value,
        CompanyId = source.OwnerCompanyId?.Value,
        Number = source.Number,
        Balance = source.Balance,
        TransactionFeeBase = source.TransactionFeeBase,
        TransactionFeeMultiplier = source.TransactionFeeMultiplier,
    };
}
