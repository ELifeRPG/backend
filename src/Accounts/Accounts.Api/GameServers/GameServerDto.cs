namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record GameServerDto
{
    public required string ClientId { get; init; }

    public required bool WhitelistEnabled { get; init; }

    public static GameServerDto Create(GameServer source) => new()
    {
        ClientId = source.ClientId,
        WhitelistEnabled = source.WhitelistEnabled,
    };
}
