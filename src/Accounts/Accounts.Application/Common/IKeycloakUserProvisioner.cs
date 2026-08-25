namespace ELifeRPG.Accounts.Application.Common;

public sealed record KeycloakRealmRole(string Name, string? Description);

public interface IKeycloakUserProvisioner
{
    ValueTask DisableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    ValueTask EnableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<KeycloakRealmRole>> ListRealmRolesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the given user's assigned realm roles (Keycloak's own built-in roles excluded), or an
    /// empty list if the user exists but has none. Returns <see langword="null"/> — distinct from an
    /// empty list — if <paramref name="keycloakUserId"/> doesn't exist in Keycloak (e.g. deleted
    /// out-of-band).
    /// </summary>
    ValueTask<IReadOnlyList<string>?> ListUserRealmRolesAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns false if <paramref name="roleName"/> doesn't exist as an assignable realm role in
    /// Keycloak (either no such role, or it's one of Keycloak's own built-in roles) — or if
    /// <paramref name="keycloakUserId"/> doesn't exist in Keycloak. These two failure cases are not
    /// distinguished at this layer.
    /// </summary>
    ValueTask<bool> AssignRealmRoleAsync(KeycloakUserId keycloakUserId, string roleName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns false if <paramref name="roleName"/> doesn't exist as a realm role in Keycloak, or if
    /// <paramref name="keycloakUserId"/> doesn't exist in Keycloak. These two failure cases are not
    /// distinguished at this layer.
    /// </summary>
    ValueTask<bool> RemoveRealmRoleAsync(KeycloakUserId keycloakUserId, string roleName, CancellationToken cancellationToken);
}
