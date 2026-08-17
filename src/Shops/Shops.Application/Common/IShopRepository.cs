using ELifeRPG.Shops.Domain.Events;

namespace ELifeRPG.Shops.Application.Common;

public interface IShopRepository
{
    ValueTask<Shop?> FindByIdAsync(ShopId shopId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Shop>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(Shop shop, ShopOpened domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
