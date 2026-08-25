using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Accounts.Application.GameServers;

/// <summary>
/// The surface other modules use to resolve the calling gameserver's OAuth client_id to its durable
/// GameServerId — see ARCHITECTURE.md §9e (cross-module reads go through Mediator contracts).
/// Returns null for an unregistered client id; callers decide whether that is fatal.
/// </summary>
public sealed record GameServerIdByClientIdQuery(string ClientId) : IRequest<GameServerId?>;

public sealed class GameServerIdByClientIdHandler(IGameServerRepository gameServerRepository)
    : IRequestHandler<GameServerIdByClientIdQuery, GameServerId?>
{
    public async ValueTask<GameServerId?> Handle(GameServerIdByClientIdQuery request, CancellationToken cancellationToken)
    {
        var server = await gameServerRepository.FindByClientIdAsync(request.ClientId, cancellationToken);
        return server?.Id;
    }
}
