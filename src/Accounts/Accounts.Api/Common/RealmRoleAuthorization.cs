using System.Security.Claims;
using System.Text.Json;

namespace ELifeRPG.Accounts.Api.Common;

/// <summary>
/// Shared realm-role check for staff-facing policies, as opposed to the client-scope checks the
/// gameserver-facing policies use elsewhere. Keycloak nests realm roles inside a single JSON object
/// claim (<c>realm_access</c>), not a flat space-delimited string like <c>scope</c>.
/// </summary>
public static class RealmRoleAuthorization
{
    public static bool HasRole(ClaimsPrincipal user, string role)
    {
        var realmAccessJson = user.FindFirst("realm_access")?.Value;
        if (realmAccessJson is null)
        {
            return false;
        }

        // A malformed realm_access claim (misconfigured claim mapper, non-standard IdP) must fail
        // closed to "no role" rather than let an unhandled JsonException surface as a 500 from
        // what's meant to be an authorization check.
        try
        {
            using var document = JsonDocument.Parse(realmAccessJson);
            return document.RootElement.TryGetProperty("roles", out var roles)
                && roles.EnumerateArray().Any(r => r.GetString() == role);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
