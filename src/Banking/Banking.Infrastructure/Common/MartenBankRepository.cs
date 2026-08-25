using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using Marten;

namespace ELifeRPG.Banking.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime, same reasoning as
/// MartenCharacterRepository — IBankingStore is a secondary Marten store, so only the default store
/// gets an auto-injected scoped IDocumentSession.
/// </summary>
public sealed class MartenBankRepository : IBankRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenBankRepository(IBankingStore store)
    {
        _session = store.LightweightSession();
    }

    // Marten infers the document id type from Bank.Id (BankId, not Guid) — pass the strongly-typed
    // id itself here, not .Value. See ARCHITECTURE.md §9e gotcha 4.
    public async ValueTask<Bank?> FindByIdAsync(BankId bankId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Bank>(bankId, cancellationToken);

    public async ValueTask<IReadOnlyList<Bank>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Bank>().ToListAsync(cancellationToken);

    public void StartStream(Bank bank, BankOpened domainEvent)
        => _session.Events.StartStream<Bank>(bank.Id.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
