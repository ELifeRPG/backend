namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record UpdateGameServerSettingsRequestDto
{
    public string? DisplayName { get; init; }

    public string? MapName { get; init; }

    public UpdateGameServerSettingsCommand ToCommand(string clientId) => new(clientId, DisplayName, MapName);
}
