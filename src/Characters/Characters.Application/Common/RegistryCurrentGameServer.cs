using ELifeRPG.Accounts.Application.GameServers;
using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Characters.Application.Common;

/// <summary>
/// Resolves the calling Bridge's client_id (via ICurrentGameServerClientId) to its registered
/// GameServerId through GameServerIdByClientIdQuery. Lives in *.Application, not *.Api, because
/// dispatching a cross-module Mediator query is only legal from an Application layer — see
/// ARCHITECTURE.md §9e. Characters.Application already references Accounts.Application for
/// AccountLookupQuery, so this dependency is honest (declared, not merely transitive).
/// Fails closed on an unregistered server rather than degrading — every endpoint that resolves this
/// already requires a gameserver:* scope, so an unregistered client_id means something is
/// misconfigured. Registered scoped, so the resolved id is looked up at most once per request.
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
