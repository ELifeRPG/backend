namespace ELifeRPG.Accounts.Application.Common;

/// <summary>
/// The backend's view of the Keycloak game-account linking SPI (keycloak-bohemia-gameaccount).
///
/// Linking itself never happens here: the player types an in-game PIN into Keycloak's own form and
/// Keycloak writes the binding onto their user. The backend only mints the PIN when an unlinked
/// player joins, and reads back which Keycloak user a Bohemia ID ended up on.
/// </summary>
public interface IBohemiaGameAccountLinker
{
    /// <summary>
    /// Mints a short-lived PIN for a Bohemia ID that no Keycloak user is bound to yet, for the mod
    /// to display. Returns null if Keycloak reports the ID is already bound — which means a binding
    /// was made between our lookup and this call, so the caller should re-resolve rather than
    /// showing the player a PIN that could never be redeemed.
    /// </summary>
    ValueTask<string?> MintLinkPinAsync(GameId bohemiaId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the Keycloak user bound to <paramref name="bohemiaId"/>, or null if unbound. Used to
    /// pick up a binding the player made in Keycloak since their last join.
    /// </summary>
    ValueTask<KeycloakUserId?> FindKeycloakUserIdAsync(GameId bohemiaId, CancellationToken cancellationToken);
}
