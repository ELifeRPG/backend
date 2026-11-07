using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Which gameserver is making the current request. No longer a tenancy key — data is hive-wide now;
/// this exists so a booking can record which server sent it. Async
/// because resolving the OAuth client_id to a durable GameServerId is a registry lookup.
/// </summary>
public interface ICurrentGameServer
{
    ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken);
}
