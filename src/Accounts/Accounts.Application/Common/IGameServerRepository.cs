namespace ELifeRPG.Accounts.Application.Common;

public interface IGameServerRepository
{
    ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken cancellationToken);

    ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken);
}
