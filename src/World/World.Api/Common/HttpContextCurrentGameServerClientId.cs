using ELifeRPG.World.Application.Common;
using Microsoft.AspNetCore.Http;

namespace ELifeRPG.World.Api.Common;

/// <summary>
/// Reads the calling Bridge's own client_id claim off the current request's JWT. Throws if it's
/// missing/empty rather than falling back to an untenanted session: every endpoint that resolves this
/// already requires a gameserver:inventory:write scope (Client Credentials tokens always populate
/// client_id), so a missing claim means something is misconfigured, not a case to silently degrade
/// for. Mirrors Characters.Api.Common/HttpContextCurrentGameServerClientId.cs and
/// Shops.Api.Common/HttpContextCurrentGameServerClientId.cs exactly — this is the narrow
/// HttpContext-reading half of <see cref="ICurrentGameServer"/>'s resolution; the registry lookup
/// that turns this raw string into a GameServerId lives in
/// World.Application/Common/RegistryCurrentGameServer.cs, because dispatching that cross-module
/// Mediator query is not legal from *.Api (ARCHITECTURE.md §9e).
/// </summary>
public sealed class HttpContextCurrentGameServerClientId(IHttpContextAccessor httpContextAccessor) : ICurrentGameServerClientId
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
