using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Characters.Application.Common;

/// <summary>
/// Which gameserver is making the current request. No longer a tenancy key — data is hive-wide now;
/// this exists so a character can record which server it is on. Async because resolving the OAuth
/// client_id to a durable
/// GameServerId is a registry lookup.
/// </summary>
public interface ICurrentGameServer
{
    ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken);
}
