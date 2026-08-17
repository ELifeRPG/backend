using ELifeRPG.Items.Application.Common;

namespace ELifeRPG.Items.Application.Items;

public sealed record ItemsQuery : IRequest<IReadOnlyList<Item>>;

public sealed class ItemsHandler(IItemRepository itemRepository) : IRequestHandler<ItemsQuery, IReadOnlyList<Item>>
{
    public async ValueTask<IReadOnlyList<Item>> Handle(ItemsQuery request, CancellationToken cancellationToken)
        => await itemRepository.FindAllAsync(cancellationToken);
}
