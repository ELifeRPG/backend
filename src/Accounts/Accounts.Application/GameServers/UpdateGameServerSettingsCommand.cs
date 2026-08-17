using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record UpdateGameServerSettingsCommand(string ClientId, bool? WhitelistEnabled) : IRequest<GameServer>;

public sealed class UpdateGameServerSettingsHandler(IGameServerRepository repository)
    : IRequestHandler<UpdateGameServerSettingsCommand, GameServer>
{
    public async ValueTask<GameServer> Handle(UpdateGameServerSettingsCommand request, CancellationToken cancellationToken)
    {
        var server = await repository.GetOrDefaultAsync(request.ClientId, cancellationToken);
        if (request.WhitelistEnabled is { } whitelistEnabled)
        {
            server.WhitelistEnabled = whitelistEnabled;
        }

        await repository.UpsertAsync(server, cancellationToken);
        return server;
    }
}
