using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain.Events;

namespace ELifeRPG.Items.Application.Items;

public union CreateItemResult(CreateItemResult.Created)
{
    public record Created(ItemId ItemId);
}

public sealed record CreateItemCommand(string DisplayName, string PrefabClassName) : IRequest<CreateItemResult>;

public sealed class CreateItemHandler(IItemRepository itemRepository) : IRequestHandler<CreateItemCommand, CreateItemResult>
{
    public async ValueTask<CreateItemResult> Handle(CreateItemCommand request, CancellationToken cancellationToken)
    {
        var itemId = new ItemId(Guid.NewGuid());
        var domainEvent = new ItemCreated(itemId, request.DisplayName, request.PrefabClassName);
        var item = Item.Create(domainEvent);

        itemRepository.StartStream(item, domainEvent);
        await itemRepository.SaveChangesAsync(cancellationToken);

        return new CreateItemResult.Created(itemId);
    }
}
