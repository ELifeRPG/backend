using ELifeRPG.Shops.Application.Common;

namespace ELifeRPG.Shops.Application.Shops;

public sealed record ShopsQuery : IRequest<IReadOnlyList<Shop>>;

public sealed class ShopsHandler(IShopRepository shopRepository) : IRequestHandler<ShopsQuery, IReadOnlyList<Shop>>
{
    public async ValueTask<IReadOnlyList<Shop>> Handle(ShopsQuery request, CancellationToken cancellationToken)
        => await shopRepository.FindAllAsync(cancellationToken);
}
