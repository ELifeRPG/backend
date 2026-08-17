# Bridge Token-Exchange Status Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make it structurally impossible for `BridgeTokenProvider` to hand back a player-impersonating token for a blocked account, regardless of what any caller does or forgets to do beforehand.

**Architecture:** `BridgeTokenProvider.ExchangeForPlayerTokenAsync` gains a required `status` parameter and returns `null` for a blocked account before ever calling Keycloak — the check moves from the caller's control flow into the one method that actually mints a token. `player-connected` simplifies to call it unconditionally and branch on whether the result is `null`.

**Tech Stack:** .NET 11 preview, ASP.NET Core Minimal APIs, Kiota-generated client (`Bridge.ApiClient`) — no other tooling needed.

**Spec:** [docs/superpowers/specs/2026-08-14-account-blocking-login-flow-design.md](../specs/2026-08-14-account-blocking-login-flow-design.md) — see its "Follow-up: closing the defense-in-depth gap" section for why this replaces a custom Keycloak SPI, and [ARCHITECTURE.md §4.3](../../../ARCHITECTURE.md#43-player-identity-token-exchange) for the underlying Keycloak finding this responds to.

## Global Constraints

- The status check must live **inside** `BridgeTokenProvider.ExchangeForPlayerTokenAsync` itself, not in `player-connected`'s control flow — that's the entire point of this change (a future caller can't skip it).
- No Keycloak, realm, or infrastructure changes of any kind. No custom SPI, no new toolchain, no container changes. That route was evaluated and explicitly rejected — see the spec.
- No new automated test project for `Bridge.Host` — it has none today, and adding one is a separate, larger, orthogonal improvement, not part of this fix. Verification here follows this repo's existing manual-curl convention for `Bridge.Host` (see `docs/bridge.md`).
- The only valid `status` values today are `"active"`/`"blocked"` (from `SessionDto.Status`, `src/Accounts/Accounts.Api/Sessions/SessionDto.cs`) — the gate checks for the literal string `"blocked"`, matching the existing check this replaces.

---

## File Structure

- `src/Bridge/Bridge.Host/BridgeTokenProvider.cs` — **modify**: `ExchangeForPlayerTokenAsync` gains a `status` parameter, returns `Task<PlayerToken?>`, gates internally.
- `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs` — **modify**: `player-connected` handler calls the new signature unconditionally, branches on `null` instead of pre-checking `session.Status`.

---

### Task 1: Fold the status gate into `ExchangeForPlayerTokenAsync`

**Files:**
- Modify: `src/Bridge/Bridge.Host/BridgeTokenProvider.cs`
- Modify: `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`

**Interfaces:**
- Produces: `BridgeTokenProvider.ExchangeForPlayerTokenAsync(string keycloakUsername, string status, CancellationToken cancellationToken = default)` returning `Task<PlayerToken?>` — `null` means "blocked, no token issued." Replaces the old `ExchangeForPlayerTokenAsync(string keycloakUsername, CancellationToken)` returning non-nullable `Task<PlayerToken>`; there are no other callers of the old signature to update (confirmed: `grep -rn "ExchangeForPlayerTokenAsync" src/` returns exactly one call site, in `SessionLocalEndpoints.cs`).
- Consumes: `ApiModels.SessionDto.KeycloakUsername`/`.Status` (both `string?`, Kiota-generated, already dereferenced with `!` elsewhere in this file per its existing convention).

- [ ] **Step 1: Update `BridgeTokenProvider.ExchangeForPlayerTokenAsync`**

Replace `src/Bridge/Bridge.Host/BridgeTokenProvider.cs` in full:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;

namespace ELifeRPG.Bridge.Host;

public sealed record PlayerToken(string AccessToken, int ExpiresInSeconds);

/// <summary>
/// Owns the Bridge's own Client Credentials token (cached, refreshed before expiry) and performs
/// the player-impersonating token exchange directly against Keycloak — never through the Central API,
/// since Keycloak requires the exchange to be authenticated by the same client the subject token was
/// issued to (see ARCHITECTURE.md §4.3).
///
/// ExchangeForPlayerTokenAsync requires the caller's already-known account status and refuses to
/// exchange for a blocked one. This check exists here, not in the caller, because Keycloak's own
/// token-exchange grant does not enforce it (verified — see ARCHITECTURE.md §4.3): a disabled user's
/// exchange still succeeds at the Keycloak layer. Putting the gate inside this method means any future
/// caller that wants a player token goes through this same check automatically, instead of every call
/// site needing to remember to check status first.
/// </summary>
public sealed class BridgeTokenProvider(HttpClient httpClient, IOptions<KeycloakOptions> options) : IAccessTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly KeycloakOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _cached;

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        => await GetOwnTokenAsync(cancellationToken);

    public async Task<string> GetOwnTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow + RefreshMargin)
        {
            return cached.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } stillCached && stillCached.ExpiresAt > DateTimeOffset.UtcNow + RefreshMargin)
            {
                return stillCached.Token;
            }

            var token = await RequestTokenAsync(
                [
                    new("client_id", _options.ClientId),
                    new("client_secret", _options.ClientSecret),
                    new("grant_type", "client_credentials"),
                ],
                cancellationToken);

            _cached = (token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds));
            return token.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlayerToken?> ExchangeForPlayerTokenAsync(string keycloakUsername, string status, CancellationToken cancellationToken = default)
    {
        if (status == "blocked")
        {
            return null;
        }

        var ownToken = await GetOwnTokenAsync(cancellationToken);

        var token = await RequestTokenAsync(
            [
                new("client_id", _options.ClientId),
                new("client_secret", _options.ClientSecret),
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", ownToken),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_subject", keycloakUsername),
            ],
            cancellationToken);

        return new PlayerToken(token.AccessToken, token.ExpiresInSeconds);
    }

    private async Task<KeycloakTokenResponse> RequestTokenAsync(KeyValuePair<string, string>[] form, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"realms/{_options.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(form),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(cancellationToken: cancellationToken);
        return token ?? throw new InvalidOperationException("Keycloak did not return a token response.");
    }

    private sealed record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);
}
```

- [ ] **Step 2: Update `player-connected` to call the new signature unconditionally**

In `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`, replace the `player-connected` mapping block:

```csharp
        app.MapPost("player-connected", async (
                PlayerConnectedRequest request,
                EliferpgApiClient apiClient,
                BridgeTokenProvider tokenProvider,
                PlayerSessionTracker sessions,
                CancellationToken cancellationToken) =>
            {
                ApiModels.SessionDto? session;
                try
                {
                    session = await apiClient.Api.Accounts.SessionBootstrap.PostAsync(
                        new ApiModels.CreateSessionRequestDto { BohemiaId = request.BohemiaId },
                        cancellationToken: cancellationToken);
                }
                // session-bootstrap no longer declares an error response (it always returns 200 with
                // a Status field instead of erroring — see Accounts.Api's session-bootstrap endpoint),
                // so Kiota emits no error mapping for this call and this catch can no longer fire in
                // practice. Left in place — harmless, and matches the identical catch shape below for
                // character-selected/player-disconnected, which still do have live error mappings.
                catch (ApiModels.ProblemDetails problem)
                {
                    return Results.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.ResponseStatusCode);
                }

                if (session is null)
                {
                    return Results.Problem("Central API returned an empty session response.");
                }

                var playerToken = await tokenProvider.ExchangeForPlayerTokenAsync(session.KeycloakUsername!, session.Status!, cancellationToken);
                if (playerToken is null)
                {
                    return Results.Ok(new PlayerConnectedResponse(session.AccountId!.Value, session.Status!, null, null));
                }

                sessions.Start(request.BohemiaId, session.AccountId!.Value);

                return Results.Ok(new PlayerConnectedResponse(
                    session.AccountId!.Value,
                    session.Status!,
                    playerToken.AccessToken,
                    playerToken.ExpiresInSeconds));
            })
            .WithName("PlayerConnected")
            .WithDescription("Local-only: stands in for the mod's 'player connected' call until real Reforger integration lands. A blocked account gets Status=\"blocked\" and no token — never a Bridge-local session. The status gate is enforced inside BridgeTokenProvider.ExchangeForPlayerTokenAsync itself, not by this handler's control flow.");
```

(Leave `character-selected` and `player-disconnected` — and everything else in this file — untouched; neither calls `ExchangeForPlayerTokenAsync`.)

- [ ] **Step 3: Build**

`dotnet` is not on the host PATH in this repo's usual dev setup — run via the devcontainer, e.g.:
```
docker exec -w /workspace eliferpg-core_devcontainer-workspace-1 dotnet build ELifeRPG.Core.slnx --nologo -v q
```
(Adjust the container name/working directory if running from a worktree — mount path is `/workspace/<relative-path-to-this-checkout>`. If another `dotnet run --project src/Bridge/Bridge.Host` process is already live in that same container — check with `docker exec <container> sh -c "pgrep -af Bridge.Host.csproj"` first — building straight into that project's own `bin/` can fail with `MSB3021`/`Text file busy`; build to a scratch output directory instead: `docker exec <container> sh -c 'scratch=$(mktemp -d) && dotnet build ELifeRPG.Core.slnx --nologo -v q -o "$scratch"; rm -rf "$scratch"'`.)

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Manual verification (matches this repo's existing curl convention for `Bridge.Host` — no automated test project exists for it)**

With `src/Api` and `src/Bridge/Bridge.Host` both running (use alternate ports if a live instance is already occupying 5100/5200 in a shared environment — see the note in Step 3):

```sh
# 1. Active account still gets a token (regression check — this path must be unaffected)
curl -s -X POST http://localhost:5200/player-connected -H "Content-Type: application/json" \
  -d '{"bohemiaId":"55555555-5555-5555-5555-555555555555"}'
# Expected: 200, {"accountId":"...","status":"active","playerAccessToken":"...","expiresInSeconds":300}

# 2. Lock that account (mint a staff-admin-dev token per docs/accounts.md, then:)
curl -X POST "http://localhost:5100/api/accounts/<accountId from above>/lock" -H "Authorization: Bearer $STAFF_TOKEN"
# Expected: 204 No Content

# 3. Blocked account gets no token, and the response shape is unchanged from before this fix
curl -s -X POST http://localhost:5200/player-connected -H "Content-Type: application/json" \
  -d '{"bohemiaId":"55555555-5555-5555-5555-555555555555"}'
# Expected: 200, {"accountId":"...","status":"blocked","playerAccessToken":null,"expiresInSeconds":null}
```

Both outcomes should be byte-for-byte identical to before this change (this is a refactor of *how* the gate is enforced, not a change to `player-connected`'s observable behavior) — the point of this verification is confirming that, not discovering new behavior.

- [ ] **Step 5: Commit**

```bash
git add src/Bridge/Bridge.Host/BridgeTokenProvider.cs src/Bridge/Bridge.Host/SessionLocalEndpoints.cs
git commit -m "feat(bridge): enforce the blocked-account gate inside ExchangeForPlayerTokenAsync

Moves the status check from player-connected's control flow into
BridgeTokenProvider.ExchangeForPlayerTokenAsync itself, so any future
caller that wants a player token goes through the same guarantee
automatically instead of needing to remember to check status first.
Closes the defense-in-depth gap noted in ARCHITECTURE.md §4.3 without
touching Keycloak — see docs/superpowers/specs/2026-08-14-account-blocking-login-flow-design.md's
\"Follow-up\" section for why a custom Keycloak SPI was rejected in favor of this."
```
