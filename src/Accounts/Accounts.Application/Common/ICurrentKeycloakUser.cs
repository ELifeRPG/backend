namespace ELifeRPG.Accounts.Application.Common;

/// <summary>
/// The Keycloak subject behind the current request, or null when the caller is not a player (a
/// gameserver or staff service account). Mirrors the ICurrentGameServerClientId pattern in the
/// Characters/Shops modules: the HttpContext-reading half lives in the Api layer, so Application
/// stays free of ASP.NET types.
/// </summary>
public interface ICurrentKeycloakUser
{
    ValueTask<KeycloakUserId?> GetIdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every game identity the caller's token claims, which may be none. A list rather than one id
    /// because a player holds one Game Identity per platform, and the realm's claim is multi-valued.
    ///
    /// The claim rides on the access token, so it only appears in a token minted <i>after</i> the
    /// binding. A player holding a portal session from before they linked presents none here — that
    /// is not a bug to guard against, it is the case the reconciler's absorb exists to reunite on
    /// the next request that does carry it.
    /// </summary>
    ValueTask<IReadOnlyList<GameIdentityId>> GetGameIdentityIdsAsync(CancellationToken cancellationToken);
}
