using ELifeRPG.Accounts.Application.Common;
using Microsoft.Extensions.Options;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class LinkUrlBuilder(IOptions<KeycloakOptions> options) : ILinkUrlBuilder
{
    /// <summary>
    /// The required action's alias, registered in the realm. Deliberately still the
    /// <c>bohemia</c>-prefixed name: renaming it would mean a realm re-registration and would break
    /// the portal's existing "Enter my PIN" button, which links to the same alias.
    /// </summary>
    private const string RequiredActionAlias = "link-bohemia-gameaccount";

    /// <summary>
    /// A dedicated public client, so the portal client's redirect URIs stay untouched — it is
    /// confidential and carries the game-identity scope. The authorization code this returns is
    /// never exchanged; the redirect exists only to land the player somewhere after linking, which
    /// is why there is no secret and no PKCE verifier anywhere in this flow.
    /// </summary>
    private const string ClientId = "eliferpg-game-link";

    private readonly KeycloakOptions _options = options.Value;

    public string BuildLinkUrl(string linkPin)
    {
        var publicBaseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var realm = Uri.EscapeDataString(_options.Realm);

        // Keycloak's account page: good enough to confirm "you are signed in and linked", and it
        // needs no dependency on the portal's URL. Must be registered as a redirect URI on ClientId.
        var redirectUri = $"{publicBaseUrl}/realms/{realm}/account/";

        var query = string.Join('&',
            $"client_id={Uri.EscapeDataString(ClientId)}",
            "response_type=code",
            "scope=openid",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"kc_action={Uri.EscapeDataString(RequiredActionAlias)}",
            $"link_pin={Uri.EscapeDataString(linkPin)}");

        return $"{publicBaseUrl}/realms/{realm}/protocol/openid-connect/auth?{query}";
    }
}
