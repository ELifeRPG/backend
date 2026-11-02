namespace ELifeRPG.Accounts.Application.Common;

/// <summary>
/// Builds the URL the mod shows in game for linking a game identity to a web account.
///
/// It is an ordinary OIDC authorization request carrying <c>kc_action</c> and the PIN, so Keycloak's
/// own login flow does the whole browser journey — including offering registration, which is the
/// common path for a join-first player. The <c>link_pin</c> parameter name is shared with the
/// keycloak-bohemia-gameaccount provider, which reads it back off the authentication session as
/// <c>client_request_param_link_pin</c>; the two ends must agree, and the provider has a test
/// pinning that key.
/// </summary>
public interface ILinkUrlBuilder
{
    string BuildLinkUrl(string linkPin);
}
