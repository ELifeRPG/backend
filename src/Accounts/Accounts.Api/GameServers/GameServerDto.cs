namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record GameServerDto(Guid Id, string ClientId, string DisplayName, string MapName)
{
    public static GameServerDto Create(GameServer source)
        => new(source.Id.Value, source.ClientId, source.DisplayName, source.MapName);
}
