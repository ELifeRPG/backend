using ELifeRPG.Items.Domain.Events;

namespace ELifeRPG.Items.Application.Common;

public interface IItemRepository
{
    ValueTask<Item?> FindByIdAsync(ItemId itemId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Item>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(Item item, ItemCreated domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
