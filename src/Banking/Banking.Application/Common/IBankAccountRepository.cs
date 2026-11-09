using ELifeRPG.Banking.Domain.Events;

namespace ELifeRPG.Banking.Application.Common;

public interface IBankAccountRepository
{
    ValueTask<BankAccount?> FindByIdAsync(BankAccountId bankAccountId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the account for a subsequent Append + SaveChangesAsync, using Marten's optimistic
    /// concurrency (FetchForWriting) so a second writer against the same account is caught at
    /// SaveChangesAsync time instead of silently lost. Use this — not FindByIdAsync — whenever the
    /// caller is about to mutate the account and Append an event. FindByIdAsync stays reserved for
    /// read-only queries that never Append.
    /// </summary>
    ValueTask<BankAccount?> FetchForUpdateAsync(BankAccountId bankAccountId, CancellationToken cancellationToken);

    /// <summary>Exact match on the canonical (compact, upper-case) number — normalise first.</summary>
    ValueTask<BankAccount?> FindByNumberAsync(string number, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<BankAccount>> FindByCharacterIdAsync(CharacterId characterId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<BankAccount>> FindByCompanyIdAsync(CompanyId companyId, CancellationToken cancellationToken);

    /// <summary>The accounts of all the given companies, in one query.</summary>
    ValueTask<IReadOnlyList<BankAccount>> FindByCompanyIdsAsync(IReadOnlyCollection<CompanyId> companyIds, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the account's own event stream back as transaction history, newest first, capped at
    /// limit. Does not itself check whether the account exists — an unknown id just yields an empty
    /// stream, so callers that need to distinguish "no transactions" from "no such account" (e.g.
    /// the Api layer's 404) should FindByIdAsync first.
    /// </summary>
    ValueTask<IReadOnlyList<BankAccountTransactionRecord>> GetHistoryAsync(BankAccountId bankAccountId, int limit, CancellationToken cancellationToken);

    void StartStream(BankAccount bankAccount, BankAccountOpened domainEvent);

    /// <summary>
    /// Reads a stored receipt straight from the database, so it also sees one a racing request just
    /// committed, including after this repository's own SaveChangesAsync failed.
    /// </summary>
    ValueTask<BookingReceipt?> FindReceiptAsync(string receiptId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a receipt in the same session as the booking's events, so SaveChangesAsync commits both or
    /// neither. A new receipt is a strict insert: if a racing request already stored one under the same
    /// id, SaveChangesAsync throws BankAccountConcurrencyException. <paramref name="replacesExpired"/>
    /// overwrites a receipt that is past its retention instead.
    /// </summary>
    void AddReceipt(BookingReceipt receipt, bool replacesExpired);

    /// <summary>
    /// Appends an event to an already-open bank account's stream. A single repository instance owns
    /// one Marten session for its whole lifetime (see MartenBankAccountRepository), so calling this
    /// for two different bank accounts before SaveChangesAsync commits both streams atomically —
    /// used by TransferHandler for the two-account balance update. See ARCHITECTURE.md §9e.
    /// </summary>
    void Append<TEvent>(BankAccountId bankAccountId, TEvent domainEvent) where TEvent : notnull;

    /// <summary>
    /// Throws BankAccountNumberTakenException when a newly opened account's number collides on the
    /// unique index, with the session's pending changes discarded so the caller can retry.
    /// </summary>
    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
