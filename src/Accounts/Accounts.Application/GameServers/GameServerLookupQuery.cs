using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.GameServers;

/// <summary>
/// For in-module (admin) use — it returns the mutable Accounts.Domain.GameServer entity, so other
/// modules must not depend on it. The cross-module surface is GameServerIdByClientIdQuery, which
/// returns only the durable GameServerId — see ARCHITECTURE.md §9e. Follows the same Found/NotFound
/// convention as AccountLookupQuery rather than throwing or silently defaulting.
/// </summary>
public union GameServerLookupResult(GameServerLookupResult.Found, GameServerLookupResult.NotFound)
{
    public record Found(GameServer Server);

    public record NotFound;
}

public sealed record GameServerLookupQuery(string ClientId) : IRequest<GameServerLookupResult>;

public sealed class GameServerLookupHandler(IGameServerRepository repository)
    : IRequestHandler<GameServerLookupQuery, GameServerLookupResult>
{
    public async ValueTask<GameServerLookupResult> Handle(GameServerLookupQuery request, CancellationToken cancellationToken)
    {
        var server = await repository.FindByClientIdAsync(request.ClientId, cancellationToken);

        return server is null
            ? new GameServerLookupResult.NotFound()
            : new GameServerLookupResult.Found(server);
    }
}
