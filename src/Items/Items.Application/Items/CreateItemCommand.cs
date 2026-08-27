using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Items.Domain.Exceptions;

namespace ELifeRPG.Items.Application.Items;

public union CreateItemResult(CreateItemResult.Created, CreateItemResult.DuplicatePrefabClassName)
{
    public record Created(ItemId ItemId);

    /// <summary>
    /// Another catalog entry already claims this prefab. Rejected rather than merged: the World
    /// module resolves a prefab to exactly one ItemId, so a second claimant would make every
    /// instance of that prefab ambiguous.
    /// </summary>
    public record DuplicatePrefabClassName(ItemId ExistingItemId);
}

public sealed record CreateItemCommand(
    string DisplayName,
    string PrefabClassName,
    ItemPersistence Persistence = ItemPersistence.Despawns) : IRequest<CreateItemResult>;

public sealed class CreateItemHandler(IItemRepository itemRepository) : IRequestHandler<CreateItemCommand, CreateItemResult>
{
    public async ValueTask<CreateItemResult> Handle(CreateItemCommand request, CancellationToken cancellationToken)
    {
        var existing = await itemRepository.FindByPrefabClassNamesAsync([request.PrefabClassName], cancellationToken);
        if (existing.Count > 0)
        {
            return new CreateItemResult.DuplicatePrefabClassName(existing[0].Id);
        }

        var itemId = new ItemId(Guid.NewGuid());
        var domainEvent = new ItemCreated(itemId, request.DisplayName, request.PrefabClassName, request.Persistence);
        var item = Item.Create(domainEvent);

        itemRepository.StartStream(item, domainEvent);

        try
        {
            await itemRepository.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicatePrefabClassNameException)
        {
            // Lost the race against a concurrent create of the same prefab. Re-read to report the
            // winner's id rather than the one this request minted and then threw away.
            var winner = await itemRepository.FindByPrefabClassNamesAsync([request.PrefabClassName], cancellationToken);
            return new CreateItemResult.DuplicatePrefabClassName(winner.Count > 0 ? winner[0].Id : itemId);
        }

        return new CreateItemResult.Created(itemId);
    }
}
