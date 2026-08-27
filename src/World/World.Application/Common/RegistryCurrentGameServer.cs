using ELifeRPG.Accounts.Application.GameServers;

namespace ELifeRPG.World.Application.Common;

/// <summary>
/// Resolves the calling Bridge's client_id (via <see cref="ICurrentGameServerClientId"/>) to its
/// registered GameServerId through <see cref="GameServerIdByClientIdQuery"/>. Lives in
/// *.Application, not *.Api, because dispatching a cross-module Mediator query is only legal from an
/// Application layer — see ARCHITECTURE.md §9e. World.Application references Accounts.Application only
/// for this query (declared honestly, not merely transitive) — mirrors
/// Characters.Application.Common/RegistryCurrentGameServer.cs and
/// Shops.Application.Common/RegistryCurrentGameServer.cs exactly. Fails closed on an unregistered
/// server rather than degrading — every endpoint that resolves this already requires a
/// gameserver:inventory:write scope, so an unregistered client_id means something is misconfigured.
/// Registered scoped, so the resolved id is looked up at most once per request.
/// </summary>
public sealed class RegistryCurrentGameServer(ICurrentGameServerClientId currentGameServerClientId, IMediator mediator)
    : ICurrentGameServer
{
    private GameServerId? _resolved;

    public async ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken)
    {
        if (_resolved is { } alreadyResolved)
        {
            return alreadyResolved;
        }

        var clientId = currentGameServerClientId.ClientId;
        var id = await mediator.Send(new GameServerIdByClientIdQuery(clientId), cancellationToken)
            ?? throw new InvalidOperationException($"No game server is registered for client_id '{clientId}'.");

        _resolved = id;
        return id;
    }
}
