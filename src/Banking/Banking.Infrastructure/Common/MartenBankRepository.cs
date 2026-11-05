using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using Marten;

namespace ELifeRPG.Banking.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime, same reasoning as
/// MartenCharacterRepository — IBankingStore is a secondary Marten store, so only the default store
/// gets an auto-injected scoped IDocumentSession.
/// </summary>
public sealed class MartenBankRepository : IBankRepository, IAsyncDisposable
{
    private readonly IQuerySession _session;

    public MartenBankRepository(IBankingStore store)
    {
        _session = store.QuerySession();
    }

    // Marten infers the document id type from Bank.Id (BankId, not Guid) — pass the strongly-typed
    // id itself here, not .Value. See ARCHITECTURE.md §9e gotcha 4.
    public async ValueTask<Bank?> FindByIdAsync(BankId bankId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Bank>(bankId, cancellationToken);

    public async ValueTask<IReadOnlyList<Bank>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Bank>().ToListAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
