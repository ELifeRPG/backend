using ELifeRPG.Items.Domain;
using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Items.Infrastructure.Common;

public sealed partial class ItemProjection : SingleStreamProjection<Item, ItemId>
{
    public static Item Create(ItemCreated domainEvent) => Item.Create(domainEvent);
}
