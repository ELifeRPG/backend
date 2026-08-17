using ELifeRPG.Companies.Application.Common;
using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Companies.Api.Common;

/// <summary>
/// Reads the calling Bridge's own client_id claim off the current request's JWT — the same claim
/// AccountEndpoints.cs already trusts for session-bootstrap. Throws if it's missing/empty rather
/// than falling back to an untenanted session: every endpoint that resolves this already requires a
/// gameserver:* scope (Client Credentials tokens always populate client_id), so a missing claim
/// means something is misconfigured, not a case to silently degrade for.
/// </summary>
public sealed class HttpContextCurrentGameServer(IHttpContextAccessor httpContextAccessor) : ICurrentGameServer
{
    public string ClientId
    {
        get
        {
            var clientId = httpContextAccessor.HttpContext?.User.FindFirst("client_id")?.Value;
            if (string.IsNullOrEmpty(clientId))
            {
                throw new InvalidOperationException("No client_id claim on the current request; cannot resolve the current gameserver.");
            }

            return clientId;
        }
    }
}
