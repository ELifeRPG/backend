using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Shops.Infrastructure.Common;

public sealed partial class ShopProjection : SingleStreamProjection<Shop, ShopId>
{
    public static Shop Create(ShopOpened domainEvent) => Shop.Create(domainEvent);
}
