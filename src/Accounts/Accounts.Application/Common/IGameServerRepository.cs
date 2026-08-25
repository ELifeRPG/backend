namespace ELifeRPG.Accounts.Application.Common;

public interface IGameServerRepository
{
    ValueTask<GameServer?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameServer>> ListAsync(CancellationToken cancellationToken);

    ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken);
}
