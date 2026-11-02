namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class KeycloakOptions
{
    /// <summary>The address the backend dials — in-cluster on the compose network.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// The published address a <i>player's browser</i> can reach, which is not the same thing as
    /// <see cref="BaseUrl"/>: that one is <c>http://keycloak:8080</c> inside compose, while the
    /// browser needs <c>http://localhost:8180</c>. Used only to build the in-game link URL, so
    /// getting it wrong ships a dead URL into the game rather than failing anything server-side —
    /// hence the startup validation.
    /// </summary>
    public required string PublicBaseUrl { get; init; }

    public required string Realm { get; init; }

    public required string ProvisioningClientId { get; init; }

    public required string ProvisioningClientSecret { get; init; }
}
