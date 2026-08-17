namespace ELifeRPG.Banking.Domain;

/// <summary>
/// A read-model row unifying BankAccountDeposited/Withdrawn/TransferredOut/TransferredIn for
/// history display — the account's own Marten event stream *is* the ledger (see MIGRATION.md §7's
/// note on collapsing legacy's separate BankAccountTransaction/BankAccountBooking split), this is
/// just a projection of it into one shape. Never appended as an event itself.
/// </summary>
public sealed record BankAccountTransactionRecord(
    DateTimeOffset OccurredAt,
    BankAccountTransactionKind Kind,
    decimal Amount,
    decimal Fee,
    BankAccountId? CounterpartyBankAccountId,
    CharacterId? ActingCharacterId);
