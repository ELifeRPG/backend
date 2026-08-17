namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record UpdateGameServerSettingsRequestDto
{
    public bool? WhitelistEnabled { get; init; }

    public UpdateGameServerSettingsCommand ToCommand(string clientId) => new(clientId, WhitelistEnabled);
}
