using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record RegisterGameServerCommand(string ClientId, string DisplayName, string MapName)
    : IRequest<GameServer>;

public sealed class RegisterGameServerHandler(IGameServerRepository gameServerRepository)
    : IRequestHandler<RegisterGameServerCommand, GameServer>
{
    public async ValueTask<GameServer> Handle(RegisterGameServerCommand request, CancellationToken cancellationToken)
    {
        var existing = await gameServerRepository.FindByClientIdAsync(request.ClientId, cancellationToken);
        if (existing is not null)
        {
            // Idempotent re-registration updates the editable fields, matching the
            // "already in that state" convention used by Lock/Unlock and StartReview.
            existing.DisplayName = request.DisplayName;
            existing.MapName = request.MapName;
            await gameServerRepository.UpsertAsync(existing, cancellationToken);
            return existing;
        }

        var server = new GameServer
        {
            Id = new GameServerId(Guid.NewGuid()),
            ClientId = request.ClientId,
            DisplayName = request.DisplayName,
            MapName = request.MapName,
        };

        await gameServerRepository.UpsertAsync(server, cancellationToken);
        return server;
    }
}
