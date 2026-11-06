namespace ELifeRPG.Banking.Domain;

/// <summary>
/// A read-model row unifying BankAccountDeposited/Withdrawn/TransferredOut/TransferredIn for
/// history display — the account's own Marten event stream *is* the ledger (see MIGRATION.md §7's
/// note on collapsing legacy's separate BankAccountTransaction/BankAccountBooking split), this is
/// just a projection of it into one shape. Never appended as an event itself.
///
/// CounterpartyNumber is the other account's number for a transfer — what a player reads on a
/// statement. The events record only the id; the number is looked up when the history is read,
/// which is safe because a number never changes once issued.
///
/// Booking identifies the request that made the row: its transaction id (shared by both legs of a
/// transfer), the game server that sent it and the optional source.
/// </summary>
public sealed record BankAccountTransactionRecord(
    DateTimeOffset OccurredAt,
    BankAccountTransactionKind Kind,
    decimal Amount,
    decimal Fee,
    BankAccountId? CounterpartyBankAccountId,
    CharacterId? ActingCharacterId,
    Booking Booking,
    string? CounterpartyNumber = null);
