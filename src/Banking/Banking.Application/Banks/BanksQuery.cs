using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.Banks;

public sealed record BanksQuery : IRequest<IReadOnlyList<Bank>>;

public sealed class BanksQueryHandler(IBankRepository bankRepository) : IRequestHandler<BanksQuery, IReadOnlyList<Bank>>
{
    public async ValueTask<IReadOnlyList<Bank>> Handle(BanksQuery request, CancellationToken cancellationToken)
        => await bankRepository.FindAllAsync(cancellationToken);
}
