using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using Marten;

namespace ELifeRPG.Shops.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime — same reasoning as
/// MartenCharacterRepository. Shops are hive-wide as of the 2026-08-22 tenancy change, so this no
/// longer scopes the session to a calling gameserver.
/// </summary>
public sealed class MartenShopRepository : IShopRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenShopRepository(IShopsStore store)
    {
        _session = store.LightweightSession();
    }

    public async ValueTask<Shop?> FindByIdAsync(ShopId shopId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Shop>(shopId, cancellationToken);

    public async ValueTask<IReadOnlyList<Shop>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Shop>().ToListAsync(cancellationToken);

    public void StartStream(Shop shop, ShopOpened domainEvent)
        => _session.Events.StartStream<Shop>(shop.Id.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
