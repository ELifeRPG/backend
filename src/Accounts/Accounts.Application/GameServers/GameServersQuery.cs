using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record GameServersQuery : IRequest<IReadOnlyList<GameServer>>;

public sealed class GameServersHandler(IGameServerRepository gameServerRepository)
    : IRequestHandler<GameServersQuery, IReadOnlyList<GameServer>>
{
    public async ValueTask<IReadOnlyList<GameServer>> Handle(GameServersQuery request, CancellationToken cancellationToken)
        => await gameServerRepository.ListAsync(cancellationToken);
}
