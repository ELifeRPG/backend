using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using JasperFx.Events;
using Marten;
using Npgsql;

namespace ELifeRPG.Banking.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime — same reasoning as
/// MartenCharacterRepository. Because it's one session per request (registered scoped/transient),
/// TransferHandler's two Append calls (source + target account) and the final SaveChangesAsync all
/// go through this same session, committing both streams atomically.
/// </summary>
public sealed class MartenBankAccountRepository : IBankAccountRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;
    private readonly NpgsqlTransaction? _crossModuleTransaction;
    private readonly Dictionary<Guid, JasperFx.Events.IEventStream<BankAccount>> _pendingStreams = new();

    public MartenBankAccountRepository(IBankingStore store)
    {
        _session = store.LightweightSession();
    }

    /// <summary>
    /// Used only by MartenBankAccountRepositoryFactory for cross-module atomic writes — the session
    /// is already bound to a shared transaction the caller owns. `crossModuleTransaction` is the same
    /// raw transaction, needed by FetchForUpdateAsync to take a Postgres row lock (Marten's
    /// FetchForWriting doesn't work on a ForTransaction-bound session — see ARCHITECTURE.md §9e
    /// gotcha 9, and MartenShopListingRepository.ReserveStockAsync for the identical pattern already
    /// proven for ShopListing). Intentionally never disposed by this class in that path; see the
    /// Global Constraints section of docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
    /// </summary>
    internal MartenBankAccountRepository(IDocumentSession session, NpgsqlTransaction crossModuleTransaction)
    {
        _session = session;
        _crossModuleTransaction = crossModuleTransaction;
    }

    public async ValueTask<BankAccount?> FindByIdAsync(BankAccountId bankAccountId, CancellationToken cancellationToken)
        => await _session.LoadAsync<BankAccount>(bankAccountId, cancellationToken);

    public async ValueTask<BankAccount?> FetchForUpdateAsync(BankAccountId bankAccountId, CancellationToken cancellationToken)
    {
        if (_crossModuleTransaction is not null)
        {
            // Row lock stands in for Marten's optimistic concurrency, which doesn't work on a
            // ForTransaction-bound session — same reasoning and syntax as
            // MartenShopListingRepository.ReserveStockAsync. The doc table's primary key is now `id`
            // alone (tenancy removed), so a single-column predicate is the index lookup — see
            // ARCHITECTURE.md §9e gotcha 9.
            var connection = _crossModuleTransaction.Connection;
            await using var lockCommand = connection!.CreateCommand();
            lockCommand.Transaction = _crossModuleTransaction;
            lockCommand.CommandText = "SELECT id FROM banking.mt_doc_bankaccount WHERE id = @id FOR UPDATE";
            lockCommand.Parameters.AddWithValue("@id", bankAccountId.Value);
            var lockedId = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (lockedId is null)
            {
                return null;
            }

            return await _session.LoadAsync<BankAccount>(bankAccountId, cancellationToken);
        }

        var stream = await _session.Events.FetchForWriting<BankAccount>(bankAccountId.Value, cancellationToken);
        if (stream.Aggregate is null)
        {
            return null;
        }

        _pendingStreams[bankAccountId.Value] = stream;

        // Deliberately NOT `stream.Aggregate` itself: BankAccountProjection is registered Inline, and
        // Marten's Inline commit re-applies this operation's newly appended event(s) onto that exact
        // instance to build the persisted snapshot (see Marten's FetchInlinedPlan.ReadIntoStream —
        // "Under Inline the commit mutates this instance"). Every domain mutator on BankAccount
        // (Deposit/Withdraw/TransferOut/ReceiveTransfer) already self-applies the event it returns, so
        // handing the caller `stream.Aggregate` to mutate would double-apply that event: once here,
        // once again by Marten at SaveChangesAsync. Loading a second, independent copy via LoadAsync
        // gives the caller something safe to mutate without touching the instance Marten owns for the
        // commit — same state as of this fetch, since no writes have happened yet. This only stays
        // decoupled from `stream.Aggregate` because UseIdentityMapForAggregates is turned off for this
        // store (see BankingInfrastructureExtensions) — with Marten's default (on), this LoadAsync
        // would return that exact same instance instead of a fresh one.
        return await _session.LoadAsync<BankAccount>(bankAccountId, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<BankAccount>> FindByCharacterIdAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<BankAccount>()
            .Where(x => x.OwnerCharacterId != null && x.OwnerCharacterId!.Value.Value == characterId.Value)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<BankAccount>> FindByCompanyIdAsync(CompanyId companyId, CancellationToken cancellationToken)
        => await _session.Query<BankAccount>()
            .Where(x => x.OwnerCompanyId != null && x.OwnerCompanyId!.Value.Value == companyId.Value)
            .ToListAsync(cancellationToken);

    // Deliberately unguarded: data is hive-wide as of the 2026-08-22 tenancy change, so
    // _session.Events.FetchStreamAsync reading any bank account's history regardless of which server
    // originally opened it is intended behavior, not a gap — there is no tenant boundary left to
    // enforce here. (Before that change, this comment noted the method was safe only because the sole
    // caller, BankAccountTransactionHistoryHandler, already resolved the account via a tenant-scoped
    // FindByIdAsync first; FindByIdAsync is no longer tenant-scoped, so that guard no longer exists —
    // and is no longer needed.)
    public async ValueTask<IReadOnlyList<BankAccountTransactionRecord>> GetHistoryAsync(
        BankAccountId bankAccountId,
        int limit,
        CancellationToken cancellationToken)
    {
        var events = await _session.Events.FetchStreamAsync(bankAccountId.Value, token: cancellationToken);

        return events
            .Select(ToTransactionRecord)
            .OfType<BankAccountTransactionRecord>()
            .OrderByDescending(x => x.OccurredAt)
            .Take(limit)
            .ToList();
    }

    private static BankAccountTransactionRecord? ToTransactionRecord(IEvent domainEvent) => domainEvent.Data switch
    {
        BankAccountDeposited deposited => new BankAccountTransactionRecord(
            domainEvent.Timestamp, BankAccountTransactionKind.Deposited, deposited.Amount, deposited.Fee, null, null),
        BankAccountWithdrawn withdrawn => new BankAccountTransactionRecord(
            domainEvent.Timestamp, BankAccountTransactionKind.Withdrawn, withdrawn.Amount, withdrawn.Fee, null, withdrawn.CharacterId),
        BankAccountTransferredOut transferredOut => new BankAccountTransactionRecord(
            domainEvent.Timestamp, BankAccountTransactionKind.TransferredOut, transferredOut.Amount, transferredOut.Fee, transferredOut.TargetBankAccountId, transferredOut.CharacterId),
        BankAccountTransferredIn transferredIn => new BankAccountTransactionRecord(
            domainEvent.Timestamp, BankAccountTransactionKind.TransferredIn, transferredIn.Amount, 0m, transferredIn.SourceBankAccountId, null),
        _ => null,
    };

    public void StartStream(BankAccount bankAccount, BankAccountOpened domainEvent)
        => _session.Events.StartStream<BankAccount>(bankAccount.Id.Value, domainEvent);

    public void Append<TEvent>(BankAccountId bankAccountId, TEvent domainEvent) where TEvent : notnull
    {
        if (_pendingStreams.TryGetValue(bankAccountId.Value, out var stream))
        {
            stream.AppendOne(domainEvent);
            return;
        }

        // Reached for: (a) cross-module writes, where the row lock already serializes access, so a
        // plain unversioned append is safe; (b) StartStream-adjacent appends in the same request that
        // never went through FetchForUpdateAsync (none exist today, but this keeps old callers safe).
        _session.Events.Append(bankAccountId.Value, domainEvent);
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (JasperFx.ConcurrencyException)
        {
            throw new BankAccountConcurrencyException("Another operation already committed against this account.");
        }
        finally
        {
            _pendingStreams.Clear();
        }
    }

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
