namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record BankAccountTransactionDto
{
    public required DateTimeOffset OccurredAt { get; init; }

    public required string Kind { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    public required Guid? CounterpartyBankAccountId { get; init; }

    public required Guid? ActingCharacterId { get; init; }

    public static BankAccountTransactionDto Create(BankAccountTransactionRecord source) => new()
    {
        OccurredAt = source.OccurredAt,
        Kind = source.Kind.ToString(),
        Amount = source.Amount,
        Fee = source.Fee,
        CounterpartyBankAccountId = source.CounterpartyBankAccountId?.Value,
        ActingCharacterId = source.ActingCharacterId?.Value,
    };
}
