using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// One game server in this hive — one server is one map. Identity is <see cref="Id"/>, not
/// <see cref="ClientId"/>: character rows reference a server durably, so rotating or renaming a
/// gameserver's Keycloak OAuth client must not orphan them. ClientId remains the Keycloak binding
/// and is unique, but is no longer the identity.
/// </summary>
public sealed class GameServer
{
    public required GameServerId Id { get; init; }

    public required string ClientId { get; init; }

    public string DisplayName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;
}
