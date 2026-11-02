namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record SessionDto
{
    /// <summary>Null only for <c>unlinked</c>, where no account exists because none could be resolved.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>
    /// Retained for compatibility and currently unused by the Bridge, which dropped the player-token
    /// exchange this once fed. Null for any account that has not linked a web identity.
    /// </summary>
    public Guid? KeycloakUserId { get; init; }

    /// <summary>The play gate, and only that: "active", "blocked", "not_whitelisted" or "unlinked".</summary>
    public required string Status { get; init; }

    /// <summary>
    /// Whether this account has a web identity. This — not <see cref="Status"/> — is the signal to
    /// show the link prompt, because an unlinked player may perfectly well be <c>active</c>.
    /// </summary>
    public required bool WebAccountLinked { get; init; }

    /// <summary>
    /// The 8-character PIN, for the player who would rather type it into the portal's form.
    /// Null when Keycloak declined to mint one (the identity was bound between our lookup and the
    /// mint) or was unreachable — in which case the player should simply rejoin.
    /// </summary>
    public string? LinkPin { get; init; }

    /// <summary>
    /// The copy-to-clipboard URL that does the whole linking flow in a browser. Set and cleared
    /// together with <see cref="LinkPin"/>. Note this is a bearer credential for the player's game
    /// identity for as long as the PIN lives, so the mod should show it on demand rather than
    /// leaving it on screen.
    /// </summary>
    public string? LinkUrl { get; init; }

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
        WebAccountLinked = source.WebAccountLinked,
        LinkPin = source.LinkPin,
        LinkUrl = source.LinkUrl,
    };
}
