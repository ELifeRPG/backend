namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record BankAccountTransactionDto
{
    /// <summary>The booking this row belongs to. Both rows of a transfer share it.</summary>
    public required Guid TransactionId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Kind { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    public required Guid? CounterpartyBankAccountId { get; init; }

    /// <summary>The other account's number, for transfers; null for deposits and withdrawals.</summary>
    public required string? CounterpartyNumber { get; init; }

    /// <summary>The character who made the booking: the depositor, the withdrawer, or the sender of a transfer. Null on the receiving side of a transfer.</summary>
    public required Guid? ActingCharacterId { get; init; }

    /// <summary>The game server that made the booking.</summary>
    public required Guid GameServerId { get; init; }

    /// <summary>The optional source sent with the booking, such as the ATM's id.</summary>
    public required string? Source { get; init; }

    public static BankAccountTransactionDto Create(BankAccountTransactionRecord source) => new()
    {
        TransactionId = source.Booking.TransactionId.Value,
        OccurredAt = source.OccurredAt,
        Kind = source.Kind.ToString(),
        Amount = source.Amount,
        Fee = source.Fee,
        CounterpartyBankAccountId = source.CounterpartyBankAccountId?.Value,
        CounterpartyNumber = source.CounterpartyNumber,
        ActingCharacterId = source.ActingCharacterId?.Value,
        GameServerId = source.Booking.GameServerId.Value,
        Source = source.Booking.Source,
    };
}
