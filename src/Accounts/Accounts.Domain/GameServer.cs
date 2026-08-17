namespace ELifeRPG.Accounts.Domain;

public sealed class GameServer
{
    public required string ClientId { get; init; }

    public bool WhitelistEnabled { get; set; }
}
