using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record GameServerLookupQuery(string ClientId) : IRequest<GameServer>;

public sealed class GameServerLookupHandler(IGameServerRepository repository) : IRequestHandler<GameServerLookupQuery, GameServer>
{
    public async ValueTask<GameServer> Handle(GameServerLookupQuery request, CancellationToken cancellationToken)
        => await repository.GetOrDefaultAsync(request.ClientId, cancellationToken);
}
