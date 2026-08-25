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
}
