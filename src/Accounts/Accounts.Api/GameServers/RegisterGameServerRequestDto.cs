using ELifeRPG.Accounts.Application.GameServers;

namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record RegisterGameServerRequestDto(string ClientId, string DisplayName, string MapName)
{
    public RegisterGameServerCommand ToCommand() => new(ClientId, DisplayName, MapName);
}
