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
    /// <summary>
    /// Multi-valued in the realm, so it arrives as one claim per identity. Name is shared with the
    /// <c>game-identity</c> client scope's protocol mapper.
    /// </summary>
    private const string GameIdentityIdsClaim = "game_identity_ids";

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

    public ValueTask<IReadOnlyList<GameIdentityId>> GetGameIdentityIdsAsync(CancellationToken cancellationToken)
    {
        var claims = httpContextAccessor.HttpContext?.User.FindAll(GameIdentityIdsClaim);
        if (claims is null)
        {
            return ValueTask.FromResult<IReadOnlyList<GameIdentityId>>([]);
        }

        // A value we cannot parse is dropped rather than failing the request: the claim is written by
        // Keycloak from a user attribute, and one malformed entry must not lock a player out of
        // every self-service path.
        var gameIdentityIds = claims
            .Select(claim => Guid.TryParse(claim.Value, out var value) ? new GameIdentityId(value) : (GameIdentityId?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .DistinctBy(x => x.Value)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<GameIdentityId>>(gameIdentityIds);
    }
}
