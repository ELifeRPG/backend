# Disconnect Token Revocation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a player disconnects, the Bridge kills their player-impersonation access token immediately, instead of leaving it valid until its ~5 minute TTL naturally expires.

**Architecture:** Central API gains an in-memory `jti` denylist (`ITokenRevocationStore`) checked once, centrally, in `src/Api`'s JWT bearer pipeline (`OnTokenValidated`). Bridge calls a new Bridge-scoped Central API endpoint on `player-disconnected`, passing the `jti`/expiry of the token it minted for that player at `player-connected`.

**Tech Stack:** ASP.NET Core `Microsoft.AspNetCore.Authentication.JwtBearer`, Mediator (source-generated CQRS), Marten/PostgreSQL (unaffected by this plan — the revocation store is in-memory, not Marten-backed), `System.IdentityModel.Tokens.Jwt` (new, Bridge-side only), Keycloak (new client scope only, no new IdP/client).

**Spec:** `docs/superpowers/specs/2026-08-14-gameserver-web-identity-linking-design.md` (Part 1 — "Revoking the player token on disconnect")

## Global Constraints

- Revocation store is in-memory, matching `PlayerSessionTracker`'s existing "lost on restart" tradeoff — no new persistence infrastructure.
- One choke point: the revocation check lives in `src/Api/Program.cs`'s single `AddJwtBearer` call, not scattered per-module.
- New Keycloak scope name: `gameserver:session:revoke`, following the existing `gameserver:<capability>:<verb>` naming convention already used for `gameserver:session:create`, `gameserver:characters:write`, etc.
- Never hand-edit `src/Bridge/Bridge.ApiClient/Generated` — it's Kiota-generated from `openapi/eliferpg-api-v1.json`, itself generated at `dotnet build` time by `src/Api`. Regenerate via `scripts/generate-bridge-client.sh` after adding the new Central API endpoint, before writing the Bridge code that calls it.
- **Execution environment:** this repo's `dotnet build`/`test`/`run`/`kiota` commands must run inside the devcontainer — `postgres`/`keycloak` hostnames and the Kiota-required .NET 10 runtime only resolve there. The container is already running as `eliferpg-core_devcontainer-workspace-1`; this worktree is visible inside it at `/workspace/.claude/worktrees/disconnect-token-revocation` (bind-mounted). Run every `dotnet`/`bash scripts/*.sh` command via:
  `docker exec -w /workspace/.claude/worktrees/disconnect-token-revocation eliferpg-core_devcontainer-workspace-1 <command>`
  Plain `git` commands (add/commit) run fine directly on the host in this worktree — no docker exec needed for those.

---

### Task 1: `ITokenRevocationStore` + in-memory implementation

**Files:**
- Create: `src/Accounts/Accounts.Application/Common/ITokenRevocationStore.cs`
- Create: `src/Accounts/Accounts.Infrastructure/Common/InMemoryTokenRevocationStore.cs`
- Modify: `src/Accounts/Accounts.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/Accounts.IntegrationTests/TokenRevocationStoreTests.cs`

**Interfaces:**
- Produces: `ITokenRevocationStore.Revoke(string jti, DateTimeOffset expiresAt)`, `ITokenRevocationStore.IsRevoked(string jti)` — consumed by Task 2's command handler and Task 4's JWT bearer hook.

- [ ] **Step 1: Write the failing test**

This test needs no live infra (no Marten/Keycloak calls) — it's placed in
`Accounts.IntegrationTests` purely because that's the project already wired
to reference `Accounts.Infrastructure`, unlike `Accounts.Domain.UnitTests`.

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Infrastructure.Common;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Unlike its sibling tests in this project, these don't need the local infra stack running —
/// InMemoryTokenRevocationStore has no external dependencies. Lives here because this is the
/// project already wired to reference Accounts.Infrastructure.
/// </summary>
public sealed class TokenRevocationStoreTests
{
    [Fact]
    public void IsRevoked_ForUnknownJti_ReturnsFalse()
    {
        var store = new InMemoryTokenRevocationStore();

        Assert.False(store.IsRevoked("unknown-jti"));
    }

    [Fact]
    public void IsRevoked_AfterRevoke_ReturnsTrue()
    {
        var store = new InMemoryTokenRevocationStore();

        store.Revoke("some-jti", DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(store.IsRevoked("some-jti"));
    }

    [Fact]
    public void IsRevoked_AfterExpiry_ReturnsFalse()
    {
        var store = new InMemoryTokenRevocationStore();

        store.Revoke("expired-jti", DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.False(store.IsRevoked("expired-jti"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter TokenRevocationStoreTests`
Expected: FAIL to compile — `ITokenRevocationStore`/`InMemoryTokenRevocationStore` don't exist yet.

- [ ] **Step 3: Write the interface and implementation**

```csharp
// src/Accounts/Accounts.Application/Common/ITokenRevocationStore.cs
namespace ELifeRPG.Accounts.Application.Common;

public interface ITokenRevocationStore
{
    void Revoke(string jti, DateTimeOffset expiresAt);

    bool IsRevoked(string jti);
}
```

```csharp
// src/Accounts/Accounts.Infrastructure/Common/InMemoryTokenRevocationStore.cs
using System.Collections.Concurrent;
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Infrastructure.Common;

/// <summary>
/// In-memory, lost on restart — same tradeoff src/Bridge/Bridge.Host/PlayerSessionTracker.cs
/// already makes. Worst case on a Central API restart, a revoked-but-not-yet-expired token briefly
/// works again, still bounded by its own original TTL (~5 min).
/// </summary>
public sealed class InMemoryTokenRevocationStore : ITokenRevocationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new();

    public void Revoke(string jti, DateTimeOffset expiresAt) => _revoked[jti] = expiresAt;

    public bool IsRevoked(string jti)
    {
        if (!_revoked.TryGetValue(jti, out var expiresAt))
        {
            return false;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            _revoked.TryRemove(jti, out _);
            return false;
        }

        return true;
    }
}
```

- [ ] **Step 4: Register it in DI**

In `src/Accounts/Accounts.Infrastructure/ServiceCollectionExtensions.cs`, inside `AddAccountInfrastructure`, add (as a singleton — it must outlive any single request scope, same reasoning as `PlayerSessionTracker` being a singleton on the Bridge side):

```csharp
services.TryAddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();
```

Add `using ELifeRPG.Accounts.Application.Common;` and `using ELifeRPG.Accounts.Infrastructure.Common;` at the top if not already present (the file is in the `Accounts.Infrastructure.Common` namespace already via its own file, so only the `Application.Common` using is actually needed).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter TokenRevocationStoreTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Accounts/Accounts.Application/Common/ITokenRevocationStore.cs \
        src/Accounts/Accounts.Infrastructure/Common/InMemoryTokenRevocationStore.cs \
        src/Accounts/Accounts.Infrastructure/ServiceCollectionExtensions.cs \
        tests/Accounts.IntegrationTests/TokenRevocationStoreTests.cs
git commit -m "feat: add in-memory token revocation store"
```

---

### Task 2: `RevokeTokenCommand`

**Files:**
- Create: `src/Accounts/Accounts.Application/Tokens/RevokeTokenCommand.cs`
- Test: `tests/Accounts.IntegrationTests/RevokeTokenCommandTests.cs`

**Interfaces:**
- Consumes: `ITokenRevocationStore` (Task 1).
- Produces: `RevokeTokenCommand(string Jti, DateTimeOffset ExpiresAt) : IRequest<Unit>` — consumed by Task 3's endpoint.

- [ ] **Step 1: Write the failing test**

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Application.Tokens;
using ELifeRPG.Accounts.Infrastructure.Common;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

public sealed class RevokeTokenCommandTests
{
    [Fact]
    public async Task Handle_RevokesTheGivenJti()
    {
        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ELifeRPG.Accounts.Application.AssemblyMarker)];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new RevokeTokenCommand("some-jti", DateTimeOffset.UtcNow.AddMinutes(5)));

        var store = provider.GetRequiredService<ITokenRevocationStore>();
        Assert.True(store.IsRevoked("some-jti"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter RevokeTokenCommandTests`
Expected: FAIL to compile — `RevokeTokenCommand` doesn't exist yet.

- [ ] **Step 3: Write the command and handler**

```csharp
// src/Accounts/Accounts.Application/Tokens/RevokeTokenCommand.cs
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Tokens;

public sealed record RevokeTokenCommand(string Jti, DateTimeOffset ExpiresAt) : IRequest<Unit>;

public sealed class RevokeTokenHandler(ITokenRevocationStore revocationStore) : IRequestHandler<RevokeTokenCommand, Unit>
{
    public ValueTask<Unit> Handle(RevokeTokenCommand request, CancellationToken cancellationToken)
    {
        revocationStore.Revoke(request.Jti, request.ExpiresAt);
        return ValueTask.FromResult(Unit.Value);
    }
}
```

(`Unit`/`IRequest<Unit>` is `Mediator`'s built-in no-payload-response type — same package already referenced via `Mediator.Abstractions`, no new dependency.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter RevokeTokenCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Accounts/Accounts.Application/Tokens/RevokeTokenCommand.cs \
        tests/Accounts.IntegrationTests/RevokeTokenCommandTests.cs
git commit -m "feat: add RevokeTokenCommand"
```

---

### Task 3: `POST /api/accounts/tokens/revoke` endpoint + Keycloak scope

**Files:**
- Modify: `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`
- Create: `src/Accounts/Accounts.Api/Sessions/RevokeTokenRequestDto.cs`
- Modify: `infra/keycloak/eliferpg-realm.json`

**Interfaces:**
- Consumes: `RevokeTokenCommand` (Task 2).
- Produces: `POST api/accounts/tokens/revoke`, gated by scope `gameserver:session:revoke` — consumed by Task 7 (Bridge, via the regenerated Kiota client).

- [ ] **Step 1: Add the request DTO**

```csharp
// src/Accounts/Accounts.Api/Sessions/RevokeTokenRequestDto.cs
using ELifeRPG.Accounts.Application.Tokens;

namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record RevokeTokenRequestDto
{
    public required string Jti { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public RevokeTokenCommand ToCommand() => new(Jti, ExpiresAt);
}
```

- [ ] **Step 2: Add the scope constant, policy, and endpoint**

In `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`, add alongside the existing `SessionCreateScope`/`SessionCreatePolicy`:

```csharp
public const string SessionRevokeScope = "gameserver:session:revoke";
private const string SessionRevokePolicy = "Accounts.SessionRevoke";
```

In `AddAccountModule`, register the new policy the same way as the existing one:

```csharp
services.AddAuthorizationBuilder()
    .AddPolicy(SessionCreatePolicy, policy => policy.RequireAssertion(context =>
        (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(SessionCreateScope)))
    .AddPolicy(SessionRevokePolicy, policy => policy.RequireAssertion(context =>
        (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(SessionRevokeScope)));
```

In `MapAccountModule`, add the endpoint to the existing `group`:

```csharp
group.MapPost("tokens/revoke", async (
        [FromBody] RevokeTokenRequestDto request,
        IMediator mediator,
        CancellationToken cancellationToken) =>
    {
        await mediator.Send(request.ToCommand(), cancellationToken);
        return Results.NoContent();
    })
    .RequireAuthorization(SessionRevokePolicy)
    .Produces(StatusCodes.Status204NoContent)
    .WithName("RevokeToken")
    .WithDescription("Revokes a specific player-impersonation token by jti, e.g. on player disconnect.");
```

- [ ] **Step 3: Grant the new scope in the Keycloak realm config**

In `infra/keycloak/eliferpg-realm.json`, add a new client scope alongside the existing `gameserver:*` ones (in the `clientScopes` array, matching the shape of `gameserver:session:create`):

```json
{
  "name": "gameserver:session:revoke",
  "protocol": "openid-connect",
  "attributes": {
    "include.in.token.scope": "true",
    "display.on.consent.screen": "false"
  }
}
```

Add `"gameserver:session:revoke"` to the `gameserver-dev` client's `defaultClientScopes` array (alongside `gameserver:session:create`, `gameserver:characters:write`, etc.).

- [ ] **Step 4: Build to confirm it compiles and regenerates the OpenAPI doc**

Run: `dotnet build`
Expected: succeeds; `openapi/eliferpg-api-v1.json` now includes the new `tokens/revoke` path (confirm with `grep -n "tokens/revoke" openapi/eliferpg-api-v1.json`).

- [ ] **Step 5: Commit**

```bash
git add src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs \
        src/Accounts/Accounts.Api/Sessions/RevokeTokenRequestDto.cs \
        infra/keycloak/eliferpg-realm.json openapi/eliferpg-api-v1.json
git commit -m "feat: add POST /api/accounts/tokens/revoke endpoint"
```

---

### Task 4: Enforce revocation in the JWT bearer pipeline

**Files:**
- Modify: `src/Api/Program.cs`

**Interfaces:**
- Consumes: `ITokenRevocationStore` (Task 1), resolved from `context.HttpContext.RequestServices` (request-scoped resolution, not constructor injection — `AddJwtBearer`'s options are configured once at startup, before DI is fully built for request scope).

- [ ] **Step 1: Add the `OnTokenValidated` hook**

In `src/Api/Program.cs`, add `using ELifeRPG.Accounts.Application.Common;` to the top, and extend the existing `AddJwtBearer` call:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters.ValidateAudience = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var jti = context.Principal?.FindFirst("jti")?.Value;
                if (jti is not null)
                {
                    var revocationStore = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationStore>();
                    if (revocationStore.IsRevoked(jti))
                    {
                        context.Fail("Token has been revoked.");
                    }
                }

                return Task.CompletedTask;
            },
        };
    });
```

No automated test for this step — this repo has no host-level (`WebApplicationFactory`) test convention yet for `src/Api`. Verified manually in Task 9's end-to-end walkthrough instead.

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/Api/Api.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Api/Program.cs
git commit -m "feat: reject requests bearing a revoked token"
```

---

### Task 5: Bridge — surface the minted token's `jti`

**Files:**
- Modify: `src/Bridge/Bridge.Host/Bridge.Host.csproj`
- Modify: `src/Bridge/Bridge.Host/BridgeTokenProvider.cs`

**Interfaces:**
- Produces: `PlayerToken` gains a `Jti` field — consumed by Task 6.

No existing automated test project covers `src/Bridge/Bridge.Host` (confirmed: it's absent from `ELifeRPG.Core.slnx`'s test folder). Verified manually alongside Task 9, matching this module's existing curl-walkthrough-only convention (`docs/bridge.md`).

- [ ] **Step 1: Add the JWT-parsing package**

In `src/Bridge/Bridge.Host/Bridge.Host.csproj`, add to the existing `ItemGroup` with `PackageReference`s:

```xml
<PackageReference Include="System.IdentityModel.Tokens.Jwt" />
```

This repo uses central package management (`Directory.Packages.props`,
`ManagePackageVersionsCentrally=true`). `System.IdentityModel.Tokens.Jwt` is
already resolved transitively (via `Microsoft.AspNetCore.Authentication.JwtBearer`
in `src/Api`) at version `8.22.0` — add it explicitly:

```xml
<PackageVersion Include="System.IdentityModel.Tokens.Jwt" Version="8.22.0" />
```

in `Directory.Packages.props`, alongside the existing `Microsoft.IdentityModel.Protocols.OpenIdConnect` entry. Then `dotnet restore src/Bridge/Bridge.Host/Bridge.Host.csproj`.

- [ ] **Step 2: Parse the `jti` claim in `BridgeTokenProvider`**

In `src/Bridge/Bridge.Host/BridgeTokenProvider.cs`, add `using System.IdentityModel.Tokens.Jwt;` at the top. Change the `PlayerToken` record and `ExchangeForPlayerTokenAsync`:

```csharp
public sealed record PlayerToken(string AccessToken, string Jti, int ExpiresInSeconds);
```

```csharp
public async Task<PlayerToken> ExchangeForPlayerTokenAsync(string keycloakUsername, CancellationToken cancellationToken = default)
{
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

    var jti = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken).Id;

    return new PlayerToken(token.AccessToken, jti, token.ExpiresInSeconds);
}
```

`JwtSecurityToken.Id` reads the token's `jti` claim directly — no signature validation performed or needed, Bridge just minted this token itself via the call above.

- [ ] **Step 3: Verify `jti` is actually present**

Matches this repo's "verify against real Keycloak" convention (`ARCHITECTURE.md` §4.3). With the local stack running (`docker compose up -d`) and the devcontainer connected, mint a token the same way `docs/bridge.md`'s `player-connected` walkthrough does, then decode it (e.g. paste into `jwt.io` or `python3 -c "import base64,json,sys; print(json.dumps(json.loads(base64.urlsafe_b64decode(sys.argv[1].split('.')[1]+'==')), indent=2))" <token>`) and confirm a `jti` claim is present. If it's ever absent for this specific token-exchange grant type, `JwtSecurityToken.Id` returns `""` (empty string, not null) — flag this to fix before Task 8, since an empty-string `jti` would make every disconnected player's revocation collide under the same key.

- [ ] **Step 4: Commit**

```bash
git add src/Bridge/Bridge.Host/Bridge.Host.csproj src/Bridge/Bridge.Host/BridgeTokenProvider.cs Directory.Packages.props
git commit -m "feat: read the jti claim off minted player tokens"
```

---

### Task 6: Bridge — track `jti`/expiry per connected player

**Files:**
- Modify: `src/Bridge/Bridge.Host/PlayerSessionTracker.cs`
- Modify: `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`

**Interfaces:**
- Consumes: `PlayerToken.Jti`/`ExpiresInSeconds` (Task 5).
- Produces: `PlayerSessionTracker.Start` gains `jti`/`expiresAt` parameters; `PlayerSession` gains `Jti`/`ExpiresAt` — consumed by Task 8.

- [ ] **Step 1: Extend `PlayerSession` and `Start`**

In `src/Bridge/Bridge.Host/PlayerSessionTracker.cs`:

```csharp
public sealed record PlayerSession(Guid AccountId, string Jti, DateTimeOffset ExpiresAt, DateTimeOffset ConnectedAt, Guid? ActiveCharacterId = null);
```

```csharp
public void Start(Guid bohemiaId, Guid accountId, string jti, DateTimeOffset expiresAt)
    => _sessions[bohemiaId] = new PlayerSession(accountId, jti, expiresAt, DateTimeOffset.UtcNow);
```

- [ ] **Step 2: Pass the new fields through in `player-connected`**

In `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`, update the `player-connected` handler:

```csharp
var playerToken = await tokenProvider.ExchangeForPlayerTokenAsync(session.KeycloakUsername!, cancellationToken);
sessions.Start(
    request.BohemiaId,
    session.AccountId!.Value,
    playerToken.Jti,
    DateTimeOffset.UtcNow.AddSeconds(playerToken.ExpiresInSeconds));
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/Bridge/Bridge.Host/Bridge.Host.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Bridge/Bridge.Host/PlayerSessionTracker.cs src/Bridge/Bridge.Host/SessionLocalEndpoints.cs
git commit -m "feat: track each connected player's token jti and expiry"
```

---

### Task 7: Regenerate the Bridge's Kiota client

**Files:**
- Regenerate (do not hand-edit): `src/Bridge/Bridge.ApiClient/Generated/**`

**Interfaces:**
- Produces: a generated request builder for `POST api/accounts/tokens/revoke` under `apiClient.Api.Accounts.Tokens.Revoke` — consumed by Task 8. (Exact generated method/namespace names depend on Kiota's output for the new OpenAPI path; confirm the actual shape after running the script, in Task 8's Step 1.)

- [ ] **Step 1: Restore the Kiota tool (first time only)**

Run: `dotnet tool restore`

- [ ] **Step 2: Regenerate**

Run: `bash scripts/generate-bridge-client.sh`

- [ ] **Step 3: Confirm the new endpoint was generated**

Run: `find src/Bridge/Bridge.ApiClient/Generated -iname "*revoke*" -o -iname "*Tokens*"`
Expected: at least one new generated file under `Api/Accounts/Tokens/...`.

- [ ] **Step 4: Build to confirm the regenerated client compiles**

Run: `dotnet build src/Bridge/Bridge.ApiClient/Bridge.ApiClient.csproj`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Bridge/Bridge.ApiClient/Generated
git commit -m "chore: regenerate Bridge API client for tokens/revoke"
```

---

### Task 8: Bridge — call revoke on disconnect

**Files:**
- Modify: `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`

**Interfaces:**
- Consumes: `PlayerSessionTracker.End` (existing), the generated `apiClient.Api.Accounts.Tokens.Revoke.PostAsync(...)` (Task 7), a generated request DTO type for the revoke body (confirm its exact generated name — likely `RevokeTokenRequestDto` under `ELifeRPG.Bridge.ApiClient.Models`, mirroring how `CreateSessionRequestDto`/`CreateCharacterRequestDto` etc. are already named in that namespace).

- [ ] **Step 1: Update `player-disconnected`**

In `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`:

```csharp
app.MapPost("player-disconnected", async (
        PlayerDisconnectedRequest request,
        EliferpgApiClient apiClient,
        PlayerSessionTracker sessions,
        CancellationToken cancellationToken) =>
    {
        var session = sessions.End(request.BohemiaId);

        if (session is not null)
        {
            await apiClient.Api.Accounts.Tokens.Revoke.PostAsync(
                new ApiModels.RevokeTokenRequestDto { Jti = session.Jti, ExpiresAt = session.ExpiresAt },
                cancellationToken: cancellationToken);
        }

        if (session?.ActiveCharacterId is { } characterId)
        {
            await apiClient.Api.Characters[characterId].Sessions.DeleteAsync(cancellationToken: cancellationToken);
        }

        return Results.Ok();
    })
    .WithName("PlayerDisconnected")
    .WithDescription("Local-only: ends the Bridge's local record of a player's connection, revokes their player-impersonation token, and ends that player's currently-selected character's session in the Central API, if one was ever selected.");
```

(`ApiModels` is already aliased at the top of this file: `using ApiModels = ELifeRPG.Bridge.ApiClient.Models;`.)

- [ ] **Step 2: Build**

Run: `dotnet build src/Bridge/Bridge.Host/Bridge.Host.csproj`
Expected: succeeds. If the generated request DTO's actual name differs from `RevokeTokenRequestDto` (Kiota sometimes disambiguates on generation), fix the reference to match — check `src/Bridge/Bridge.ApiClient/Generated/Models/` for the actual generated class name.

- [ ] **Step 3: Commit**

```bash
git add src/Bridge/Bridge.Host/SessionLocalEndpoints.cs
git commit -m "feat: revoke the player's token on disconnect"
```

---

### Task 9: End-to-end manual verification

**Files:** none (verification only)

- [ ] **Step 1: Start the stack**

The compose stack (`postgres`, `keycloak`, `otel-collector`, etc.) is already
running via `eliferpg-core_devcontainer-workspace-1`'s compose project — no
need to run `docker compose up -d` again. Just run the two apps, inside the
devcontainer, from this worktree:

```bash
docker exec -w /workspace/.claude/worktrees/disconnect-token-revocation eliferpg-core_devcontainer-workspace-1 \
  dotnet run --project src/Api/Api.csproj &
docker exec -w /workspace/.claude/worktrees/disconnect-token-revocation eliferpg-core_devcontainer-workspace-1 \
  dotnet run --project src/Bridge/Bridge.Host/Bridge.Host.csproj &
```

- [ ] **Step 2: Connect a player and capture the token**

```bash
curl -s -X POST http://localhost:5200/player-connected \
  -H "Content-Type: application/json" \
  -d '{"bohemiaId":"11111111-1111-1111-1111-111111111111"}'
```

Save the returned `playerAccessToken`.

- [ ] **Step 3: Confirm the token works against the Central API**

Use it against any proxied endpoint per `docs/accounts.md`'s convention, e.g.:

```bash
curl -s http://localhost:5100/api/accounts/<accountId>/characters \
  -H "Authorization: Bearer <playerAccessToken>"
```

Expected: succeeds (not `401`).

- [ ] **Step 4: Disconnect and immediately replay the same token**

```bash
curl -X POST http://localhost:5200/player-disconnected \
  -H "Content-Type: application/json" \
  -d '{"bohemiaId":"11111111-1111-1111-1111-111111111111"}'

curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5100/api/accounts/<accountId>/characters \
  -H "Authorization: Bearer <playerAccessToken>"
```

Expected: `401`, immediately — not after waiting out the ~5 minute TTL.

- [ ] **Step 5: Update `docs/bridge.md`**

Add a short note to the "Connection and session lifecycle" section documenting that `player-disconnected` now also revokes the player's access token immediately (Central API rejects it on the very next request), not just ending the Bridge-local session record and character session.
