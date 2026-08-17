namespace ELifeRPG.Banking.Api.BankAccounts;

/// <summary>Shared response shape for deposit/withdraw/transfer — none of these map from a single domain object, just the union case's own fields.</summary>
public sealed record TransactionResultDto
{
    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    public required decimal NewBalance { get; init; }
}
