namespace ELifeRPG.Banking.Api.BankAccounts;

/// <summary>Shared response shape for deposit/withdraw/transfer — none of these map from a single domain object, just the union case's own fields.</summary>
public sealed record TransactionResultDto
{
    /// <summary>Identifies this booking; the matching ledger row carries the same id.</summary>
    public required Guid TransactionId { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    public required decimal NewBalance { get; init; }
}
