using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Shops.Application.Common;

/// <summary>
/// Which gameserver is making the current request. No longer a tenancy key — data is hive-wide now;
/// this exists so a shop can record which server it was opened on (Task 10's Shop.ServerId). Async
/// because resolving the OAuth client_id to a durable GameServerId is a registry lookup.
/// </summary>
public interface ICurrentGameServer
{
    ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken);
}
