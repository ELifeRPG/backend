using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// Backs <c>GET /api/inventory/characters/{characterId}/items</c> — the flat, unpaginated set of
/// live instances rooted at a character. See <see cref="IItemInstanceRepository.FindCarriedByRootCharacterAsync"/>
/// for exactly which rows that excludes and why this is never paged.
/// </summary>
public sealed record CarriedInventoryQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<ItemInstance>>;

public sealed class CarriedInventoryHandler(IItemInstanceRepository repository, TimeProvider timeProvider)
    : IRequestHandler<CarriedInventoryQuery, IReadOnlyList<ItemInstance>>
{
    public async ValueTask<IReadOnlyList<ItemInstance>> Handle(CarriedInventoryQuery request, CancellationToken cancellationToken)
        => await repository.FindCarriedByRootCharacterAsync(request.CharacterId, timeProvider.GetUtcNow(), cancellationToken);
}
