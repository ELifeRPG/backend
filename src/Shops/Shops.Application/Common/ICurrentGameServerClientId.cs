namespace ELifeRPG.Shops.Application.Common;

/// <summary>
/// The calling Bridge's own OAuth client_id claim off the current request — the raw string, before
/// resolution to a durable GameServerId. Kept separate from ICurrentGameServer so the
/// registry-lookup logic (which needs to dispatch a cross-module Mediator query, only legal from
/// *.Application per ARCHITECTURE.md §9e) can live in this module instead of in Shops.Api, which
/// must not reference Accounts.Application directly. Implementations should fail closed (throw) if
/// the claim is missing rather than degrade — every endpoint that resolves this already requires a
/// gameserver:* scope, so a missing claim means something is misconfigured.
/// </summary>
public interface ICurrentGameServerClientId
{
    string ClientId { get; }
}
