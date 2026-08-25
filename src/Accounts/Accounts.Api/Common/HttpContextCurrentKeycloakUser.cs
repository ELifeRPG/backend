using System.Security.Claims;
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Accounts.Api.Common;

/// <summary>
/// Reads the Keycloak subject from the bearer token. Both claim names are checked because
/// ASP.NET's JwtBearer handler rewrites <c>sub</c> to <see cref="ClaimTypes.NameIdentifier"/>
/// unless inbound claim mapping is turned off — so which one is present depends on host
/// configuration, not on the token.
/// </summary>
public sealed class HttpContextCurrentKeycloakUser(IHttpContextAccessor httpContextAccessor) : ICurrentKeycloakUser
{
    public ValueTask<KeycloakUserId?> GetIdAsync(CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var subject = user?.FindFirst("sub")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // A service-account token's subject is the service account's own user, which is a real
        // Keycloak user id but never has an Account. Callers resolve that to "no account" rather
        // than treating it as a player, so no extra filtering is needed here.
        return ValueTask.FromResult(
            Guid.TryParse(subject, out var id) ? new KeycloakUserId(id) : (KeycloakUserId?)null);
    }
}
