using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain;
using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Items.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime — same reasoning as
/// MartenCompanyRepository. Hive model: the item catalog is shared across every gameserver, so the
/// session is untenanted (the parameterless LightweightSession() overload) — see
/// docs/superpowers/specs/2026-08-22-hive-tenancy-design.md.
/// </summary>
public sealed class MartenItemRepository : IItemRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenItemRepository(IItemsStore store)
    {
        _session = store.LightweightSession();
    }

    public async ValueTask<Item?> FindByIdAsync(ItemId itemId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Item>(itemId, cancellationToken);

    public async ValueTask<IReadOnlyList<Item>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Item>().ToListAsync(cancellationToken);

    public void StartStream(Item item, ItemCreated domainEvent)
        => _session.Events.StartStream<Item>(item.Id.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
