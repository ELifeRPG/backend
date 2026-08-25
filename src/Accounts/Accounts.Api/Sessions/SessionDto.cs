namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record SessionDto
{
    /// <summary>Null when <see cref="Status"/> is <c>unlinked</c> — no account owns this Bohemia ID yet.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>
    /// The subject the Bridge impersonates via Keycloak's token exchange. A user id rather than a
    /// username: portal-created users are named by the player, and the backend knows them by id.
    /// Null when unlinked.
    /// </summary>
    public Guid? KeycloakUserId { get; init; }

    public required string Status { get; init; }

    /// <summary>
    /// Set only for <c>unlinked</c>, for the mod to display. Null if Keycloak declined to mint one
    /// (the Bohemia ID was bound between our lookup and the mint), in which case the player should
    /// simply rejoin.
    /// </summary>
    public string? LinkPin { get; init; }

    public static SessionDto Create(CreateSessionResponse source) => new()
    {
        AccountId = source.AccountId?.Value,
        KeycloakUserId = source.KeycloakUserId?.Value,
        Status = source.Status switch
        {
            SessionStatus.Blocked => "blocked",
            SessionStatus.NotWhitelisted => "not_whitelisted",
            SessionStatus.Unlinked => "unlinked",
            _ => "active",
        },
        LinkPin = source.LinkPin,
    };
}
