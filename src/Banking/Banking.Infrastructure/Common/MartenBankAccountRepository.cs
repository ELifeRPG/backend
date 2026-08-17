using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Shared.Kernel;
using JasperFx.Events;
using Marten;

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

    public MartenBankAccountRepository(IBankingStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }

    /// <summary>
    /// Used only by MartenBankAccountRepositoryFactory for cross-module atomic writes — the session
    /// is already bound to a shared transaction the caller owns. Intentionally never disposed by
    /// this class in that path; see Global Constraints in
    /// docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
    /// </summary>
    internal MartenBankAccountRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask<BankAccount?> FindByIdAsync(BankAccountId bankAccountId, CancellationToken cancellationToken)
        => await _session.LoadAsync<BankAccount>(bankAccountId, cancellationToken);

    public async ValueTask<IReadOnlyList<BankAccount>> FindByCharacterIdAsync(CharacterId characterId, CancellationToken cancellationToken)
        => await _session.Query<BankAccount>()
            .Where(x => x.OwnerCharacterId != null && x.OwnerCharacterId!.Value.Value == characterId.Value)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<BankAccount>> FindByCompanyIdAsync(CompanyId companyId, CancellationToken cancellationToken)
        => await _session.Query<BankAccount>()
            .Where(x => x.OwnerCompanyId != null && x.OwnerCompanyId!.Value.Value == companyId.Value)
            .ToListAsync(cancellationToken);

    // Safe only because the sole caller (BankAccountTransactionHistoryHandler) already resolves the
    // account via a tenant-scoped FindByIdAsync first — a future direct caller of this method would
    // bypass that guard, since _session.Events.FetchStreamAsync itself has no tenant check of its own.
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
        => _session.Events.Append(bankAccountId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
