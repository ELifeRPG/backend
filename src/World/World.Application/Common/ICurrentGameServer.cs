namespace ELifeRPG.World.Application.Common;

/// <summary>
/// Which gameserver is making the current request — used by the ack path
/// (<c>AcknowledgeSpawnsHandler</c>) and the negative-ack path (<c>SpawnFailedHandler</c>) both to
/// stamp <c>ItemInstance.RootGameServerId</c> at delivery time and as the server-guard's own identity
/// (the id the batched <c>CharactersOnServerQuery</c> checks each acked character's
/// <c>Character.CurrentServerId</c> against). Async because resolving the OAuth client_id to a durable
/// GameServerId is a registry lookup. Duplicated per module by design — see
/// Characters.Application.Common/ICurrentGameServer.cs and
/// Shops.Application.Common/ICurrentGameServer.cs, which this mirrors exactly.
/// </summary>
public interface ICurrentGameServer
{
    ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken);
}
