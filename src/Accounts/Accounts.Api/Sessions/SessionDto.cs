namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record SessionDto
{
    public required Guid AccountId { get; init; }

    public required string KeycloakUsername { get; init; }

    public required string Status { get; init; }

    public static SessionDto Create(CreateSessionResponse source) => new()
    {
        AccountId = source.AccountId.Value,
        KeycloakUsername = source.KeycloakUsername,
        Status = source.Status switch
        {
            SessionStatus.Blocked => "blocked",
            SessionStatus.NotWhitelisted => "not_whitelisted",
            _ => "active",
        },
    };
}
