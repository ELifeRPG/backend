# Account Blocking & Login Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator lock/unlock an `Account` over HTTP, and make `player-connected` report a blocked account as ordinary data (`{status: "blocked"}`, no token) instead of an opaque error.

**Architecture:** `CreateSessionCommand`'s result collapses to a single response record carrying `AccountStatus`; a new `LockAccountCommand`/`UnlockAccountCommand` pair calls the existing `Account.Lock()`/`Unlock()` domain methods plus new Keycloak admin-API calls that disable/enable the underlying Keycloak user (so no new player token can ever be issued post-lock). Enforcement is centralized at token issuance (short TTL + Keycloak disablement) — no changes to `Characters`/`Banking`/`Companies` write handlers, no Bridge poll loop.

**Tech Stack:** .NET 11 preview, Mediator (native `union` result types), Marten/Postgres event sourcing, Keycloak (admin REST API + OIDC), Kiota-generated Bridge client, xUnit integration tests against live Postgres/Keycloak.

**Spec:** [docs/superpowers/specs/2026-08-14-account-blocking-login-flow-design.md](../specs/2026-08-14-account-blocking-login-flow-design.md)

## Global Constraints

- Domain keeps `AccountStatus.Locked`/`Account.Lock()`/`Unlock()` — only outward-facing DTOs render it as `"blocked"`/`"active"`.
- Locking calls Keycloak's `PUT /admin/realms/{realm}/users/{id}` `{enabled: false}` (disables the user, blocking any future token-exchange) — not `.../logout` (which only revokes sessions/refresh tokens, irrelevant to the Bridge's client-credentials + token-exchange flow).
- Lock/unlock HTTP endpoints require a **new** Keycloak scope (`accounts:manage`) on a **new** client — never folded into `gameserver-dev`'s scopes.
- A blocked account gets **no** player-access token from `player-connected` — not a token with downstream enforcement.
- Out of scope, do not touch: any `Characters`/`Banking`/`Companies` write-handler account-status checks, any Bridge poll loop, real ArmA Reforger mod integration.
- The realm's `accessTokenLifespan` is already `300` (5 minutes, confirmed in `infra/keycloak/eliferpg-realm.json`) — this already satisfies the spec's TTL requirement; no realm-wide config change needed in any task below.

---

## File Structure

- `src/Accounts/Accounts.Application/Sessions/CreateSessionCommand.cs` — **modify**: union → single `CreateSessionResponse` record.
- `src/Accounts/Accounts.Api/Sessions/SessionDto.cs` — **modify**: add `Status` string field.
- `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs` — **modify**: session-bootstrap always 200; new lock/unlock endpoints + `accounts:manage` policy.
- `src/Accounts/Accounts.Application/Common/IKeycloakUserProvisioner.cs`, `src/Accounts/Accounts.Infrastructure/Common/KeycloakUserProvisioner.cs` — **modify**: add `DisableUserAsync`/`EnableUserAsync`.
- `src/Accounts/Accounts.Application/Common/IAccountRepository.cs`, `src/Accounts/Accounts.Infrastructure/Common/MartenAccountRepository.cs` — **modify**: add `Append<TEvent>`.
- `src/Accounts/Accounts.Application/Accounts/LockAccountCommand.cs`, `UnlockAccountCommand.cs` — **create**.
- `infra/keycloak/eliferpg-realm.json` — **modify**: new `accounts:manage` clientScope + `staff-admin-dev` client.
- `src/Bridge/Bridge.ApiClient/Generated/**` — **regenerate** (Kiota, not hand-edited).
- `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs` — **modify**: `PlayerConnectedResponse` gains `Status`, nullable token; `character-selected`/`player-disconnected` gain error translation.
- `docs/accounts.md`, `docs/bridge.md` — **modify**: document new response shape and endpoints.
- Tests: `tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs` (modify), `KeycloakTestClient.cs` (modify), `KeycloakUserProvisionerTests.cs` (create), `LockAccountCommandTests.cs` (create); four call sites in `tests/{Companies,Characters,Banking}.IntegrationTests/*.cs` (modify, identical mechanical change).

---

### Task 1: `session-bootstrap` reports status instead of erroring

**Files:**
- Modify: `src/Accounts/Accounts.Application/Sessions/CreateSessionCommand.cs`
- Modify: `src/Accounts/Accounts.Api/Sessions/SessionDto.cs`
- Modify: `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`
- Modify (test): `tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs`
- Modify (test): `tests/Companies.IntegrationTests/CompanyCommandTests.cs:251-264`
- Modify (test): `tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs:130-139`
- Modify (test): `tests/Banking.IntegrationTests/BankingCommandTests.cs:232-241`
- Modify (test): `tests/Banking.IntegrationTests/CorporateBankAccountTests.cs:179-188`

**Interfaces:**
- Produces: `CreateSessionResponse(AccountId AccountId, string KeycloakUsername, AccountStatus Status)` — replaces the `CreateSessionResult` union everywhere. `CreateSessionCommand : IRequest<CreateSessionResponse>`.
- Produces: `SessionDto.Create(CreateSessionResponse source)` (signature change from `CreateSessionResult.Created`).

- [ ] **Step 1: Update the primary test file to the new shape (will fail to compile)**

Replace the two test methods and the `Send` helper in `tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs`:

```csharp
    [Fact]
    public async Task Handle_NewBohemiaId_CreatesAccountWithExpectedKeycloakUsername()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(KeycloakUsername.For(bohemiaId), result.KeycloakUsername);
        Assert.Equal(AccountStatus.Active, result.Status);
    }

    [Fact]
    public async Task Handle_CalledTwiceForSameBohemiaId_ReturnsSameAccountIdWithoutDuplicating()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var first = await Send(new CreateSessionCommand(bohemiaId));
        var second = await Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(first.KeycloakUsername);
        Assert.Equal(first.AccountId, second.AccountId);
    }

    private async Task<CreateSessionResponse> Send(CreateSessionCommand command)
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
```

- [ ] **Step 2: Confirm it fails to build**

Run: `dotnet build tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj`
Expected: FAIL — `CS0246: The type or namespace name 'CreateSessionResponse' could not be found` (production code doesn't define it yet).

- [ ] **Step 3: Collapse `CreateSessionCommand`'s result to a single record**

Replace `src/Accounts/Accounts.Application/Sessions/CreateSessionCommand.cs` in full:

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Sessions;

public sealed record CreateSessionResponse(AccountId AccountId, string KeycloakUsername, AccountStatus Status);

public sealed record CreateSessionCommand(GameId BohemiaId) : IRequest<CreateSessionResponse>;

public sealed class CreateSessionHandler(
    IAccountRepository accountRepository,
    IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<CreateSessionCommand, CreateSessionResponse>
{
    public async ValueTask<CreateSessionResponse> Handle(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByBohemiaIdAsync(request.BohemiaId, cancellationToken);

        if (account is null)
        {
            var keycloakUserId = await keycloakUserProvisioner.EnsureUserAsync(request.BohemiaId, cancellationToken);
            var accountId = new AccountId(Guid.NewGuid());
            var @event = new AccountCreated(accountId, request.BohemiaId, keycloakUserId);

            account = Account.Create(@event);
            accountRepository.StartStream(account, @event);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        return new CreateSessionResponse(account.Id, KeycloakUsername.For(account.BohemiaId), account.Status);
    }
}
```

- [ ] **Step 4: Update `SessionDto` to carry `Status`**

Replace `src/Accounts/Accounts.Api/Sessions/SessionDto.cs` in full:

```csharp
namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record SessionDto
{
    public required Guid AccountId { get; init; }

    public required string KeycloakUsername { get; init; }

    public required string Status { get; init; }

    public static SessionDto Create(CreateSessionResponse source) => new()
    {
        AccountId = source.AccountId.Value,
        KeycloakUsername = source.KeycloakUsername,
        Status = source.Status == AccountStatus.Locked ? "blocked" : "active",
    };
}
```

- [ ] **Step 5: Update the `session-bootstrap` endpoint to always return 200**

In `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`, replace the `session-bootstrap` mapping block:

```csharp
        group.MapPost("session-bootstrap", async (
                [FromBody] CreateSessionRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(SessionDto.Create(result));
            })
            .RequireAuthorization(SessionCreatePolicy)
            .Produces<SessionDto>()
            .WithName("BootstrapSession")
            .WithDescription("Bootstraps (or looks up) a session for a player's Bohemia ID, provisioning an account if needed. Always returns 200 — a blocked account is reported via the Status field, not an error.");
```

(Leave the rest of the file — `AddAccountModule`, the `SessionCreatePolicy`/scope constant — untouched for now; Task 4 adds the lock/unlock policy alongside it.)

- [ ] **Step 6: Fix the four downstream test helpers**

`CompanyCommandTests.cs`, `CreateCharacterCommandTests.cs`, `BankingCommandTests.cs`, and `CorporateBankAccountTests.cs` each have an identical private `CreateActiveAccountAsync(IMediator mediator)` helper. In each file, replace:

```csharp
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        Assert.True(result is CreateSessionResult.Created, $"Expected Created, got {result}");
        if (result is not CreateSessionResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        _createdUsernames.Add(created.KeycloakUsername);

        return created.AccountId;
```

with:

```csharp
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
```

- [ ] **Step 7: Build the whole solution and confirm it compiles**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Run the integration tests (requires local infra — see README)**

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj`
Expected: PASS. If the local infra stack (`docker compose up -d`, devcontainer connected to its network) isn't available, skip this step — Step 7's build is the required gate; note in the task summary that integration verification was skipped and why.

- [ ] **Step 9: Commit**

```bash
git add src/Accounts/Accounts.Application/Sessions/CreateSessionCommand.cs \
        src/Accounts/Accounts.Api/Sessions/SessionDto.cs \
        src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs \
        tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs \
        tests/Companies.IntegrationTests/CompanyCommandTests.cs \
        tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs \
        tests/Banking.IntegrationTests/BankingCommandTests.cs \
        tests/Banking.IntegrationTests/CorporateBankAccountTests.cs
git commit -m "feat(accounts): session-bootstrap reports account status instead of erroring on locked"
```

---

### Task 2: Keycloak user disable/enable capability

**Files:**
- Modify: `src/Accounts/Accounts.Application/Common/IKeycloakUserProvisioner.cs`
- Modify: `src/Accounts/Accounts.Infrastructure/Common/KeycloakUserProvisioner.cs`
- Modify: `tests/Accounts.IntegrationTests/KeycloakTestClient.cs`
- Test: `tests/Accounts.IntegrationTests/KeycloakUserProvisionerTests.cs`

**Interfaces:**
- Consumes: `KeycloakUserId` (`src/Accounts/Accounts.Domain/KeycloakUserId.cs`, `.Value` gives the underlying `Guid`); `KeycloakOptions` (`BaseUrl`/`Realm`/`ProvisioningClientId`/`ProvisioningClientSecret`).
- Produces: `IKeycloakUserProvisioner.DisableUserAsync(KeycloakUserId, CancellationToken)` / `EnableUserAsync(KeycloakUserId, CancellationToken)` — used by Task 3.

- [ ] **Step 1: Write the failing test**

Create `tests/Accounts.IntegrationTests/KeycloakUserProvisionerTests.cs`:

```csharp
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Infrastructure.Common;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Options;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class KeycloakUserProvisionerTests : IAsyncLifetime
{
    private readonly KeycloakTestClient _keycloak = new();
    private KeycloakUserProvisioner _provisioner = null!;
    private string _username = null!;
    private KeycloakUserId _keycloakUserId;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new KeycloakOptions
        {
            BaseUrl = "http://keycloak:8080/",
            Realm = "eliferpg",
            ProvisioningClientId = "account-service",
            ProvisioningClientSecret = "account-service-secret",
        });
        _provisioner = new KeycloakUserProvisioner(new HttpClient { BaseAddress = new Uri(options.Value.BaseUrl) }, options);

        var bohemiaId = new GameId(Guid.NewGuid());
        _username = KeycloakUsername.For(bohemiaId);
        _keycloakUserId = await _provisioner.EnsureUserAsync(bohemiaId, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _keycloak.DeleteUserAsync(_username);

    [Fact]
    public async Task DisableUserAsync_DisablesTheKeycloakUser()
    {
        await _provisioner.DisableUserAsync(_keycloakUserId, CancellationToken.None);

        Assert.False(await _keycloak.GetUserEnabledAsync(_username));
    }

    [Fact]
    public async Task EnableUserAsync_AfterDisabling_ReEnablesTheKeycloakUser()
    {
        await _provisioner.DisableUserAsync(_keycloakUserId, CancellationToken.None);

        await _provisioner.EnableUserAsync(_keycloakUserId, CancellationToken.None);

        Assert.True(await _keycloak.GetUserEnabledAsync(_username));
    }
}
```

Add `GetUserEnabledAsync` to `tests/Accounts.IntegrationTests/KeycloakTestClient.cs` — extend the existing `KeycloakUserRepresentation` record with `Enabled` and add the method:

```csharp
    private sealed record KeycloakUserRepresentation(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("enabled")] bool Enabled);
```

(replaces the current single-property record) and add this method to the class, alongside `DeleteUserAsync`:

```csharp
    public async Task<bool> GetUserEnabledAsync(string username)
    {
        var adminToken = await GetAdminTokenAsync();

        using var lookupRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"admin/realms/eliferpg/users?username={Uri.EscapeDataString(username)}&exact=true");
        lookupRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var lookupResponse = await _httpClient.SendAsync(lookupRequest);
        lookupResponse.EnsureSuccessStatusCode();

        var users = await lookupResponse.Content.ReadFromJsonAsync<List<KeycloakUserRepresentation>>();
        var user = users?.SingleOrDefault() ?? throw new InvalidOperationException($"Keycloak user '{username}' not found.");
        return user.Enabled;
    }
```

- [ ] **Step 2: Confirm it fails to build**

Run: `dotnet build tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj`
Expected: FAIL — `CS1061: 'KeycloakUserProvisioner' does not contain a definition for 'DisableUserAsync'`.

- [ ] **Step 3: Add the methods to the interface**

In `src/Accounts/Accounts.Application/Common/IKeycloakUserProvisioner.cs`, add to the interface:

```csharp
namespace ELifeRPG.Accounts.Application.Common;

public interface IKeycloakUserProvisioner
{
    ValueTask<KeycloakUserId> EnsureUserAsync(GameId bohemiaId, CancellationToken cancellationToken);

    ValueTask DisableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    ValueTask EnableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement them in `KeycloakUserProvisioner`**

In `src/Accounts/Accounts.Infrastructure/Common/KeycloakUserProvisioner.cs`, add these methods to the class (after `EnsureUserAsync`, before `GetAdminTokenAsync`):

```csharp
    public async ValueTask DisableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await SetUserEnabledAsync(keycloakUserId, enabled: false, cancellationToken);

    public async ValueTask EnableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await SetUserEnabledAsync(keycloakUserId, enabled: true, cancellationToken);

    private async ValueTask SetUserEnabledAsync(KeycloakUserId keycloakUserId, bool enabled, CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"admin/realms/{_options.Realm}/users/{keycloakUserId.Value}")
        {
            Content = JsonContent.Create(new { enabled }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
```

- [ ] **Step 5: Build and run the test (requires local infra)**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded.

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter KeycloakUserProvisionerTests`
Expected: PASS (2 tests). If local infra isn't available, skip and note it — the build in this step is the required gate.

- [ ] **Step 6: Commit**

```bash
git add src/Accounts/Accounts.Application/Common/IKeycloakUserProvisioner.cs \
        src/Accounts/Accounts.Infrastructure/Common/KeycloakUserProvisioner.cs \
        tests/Accounts.IntegrationTests/KeycloakTestClient.cs \
        tests/Accounts.IntegrationTests/KeycloakUserProvisionerTests.cs
git commit -m "feat(accounts): add Keycloak user disable/enable to the provisioner"
```

---

### Task 3: `LockAccountCommand` / `UnlockAccountCommand`

**Files:**
- Modify: `src/Accounts/Accounts.Application/Common/IAccountRepository.cs`
- Modify: `src/Accounts/Accounts.Infrastructure/Common/MartenAccountRepository.cs`
- Create: `src/Accounts/Accounts.Application/Accounts/LockAccountCommand.cs`
- Create: `src/Accounts/Accounts.Application/Accounts/UnlockAccountCommand.cs`
- Test: `tests/Accounts.IntegrationTests/LockAccountCommandTests.cs`

**Interfaces:**
- Consumes: `IKeycloakUserProvisioner.DisableUserAsync`/`EnableUserAsync` (Task 2); `Account.Lock()`/`Unlock()` (already exist, `src/Accounts/Accounts.Domain/Account.cs`); `CreateSessionCommand`/`CreateSessionResponse` (Task 1, used by the test to observe status after locking).
- Produces: `LockAccountCommand(AccountId AccountId) : IRequest<LockAccountResult>` with `union LockAccountResult(Locked, AccountNotFound)`; `UnlockAccountCommand(AccountId AccountId) : IRequest<UnlockAccountResult>` with `union UnlockAccountResult(Unlocked, AccountNotFound)`. Used by Task 4's endpoints.
- Produces: `IAccountRepository.Append<TEvent>(AccountId accountId, TEvent @event) where TEvent : notnull`.

- [ ] **Step 1: Write the failing test**

Create `tests/Accounts.IntegrationTests/LockAccountCommandTests.cs`:

```csharp
using ELifeRPG.Accounts.Application.Accounts;
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class LockAccountCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
                ["Keycloak:Realm"] = "eliferpg",
                ["Keycloak:ProvisioningClientId"] = "account-service",
                ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ELifeRPG.Accounts.Application.AssemblyMarker)];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var username in _createdUsernames)
        {
            await _keycloak.DeleteUserAsync(username);
        }

        await _provider.DisposeAsync();
    }

    private async Task<(AccountId AccountId, GameId BohemiaId, string KeycloakUsername)> CreateAccountAsync()
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var response = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        _createdUsernames.Add(response.KeycloakUsername);
        return (response.AccountId, bohemiaId, response.KeycloakUsername);
    }

    [Fact]
    public async Task Handle_ActiveAccount_LocksItAndDisablesTheKeycloakUser()
    {
        var (accountId, bohemiaId, username) = await CreateAccountAsync();

        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(result is LockAccountResult.Locked, $"Expected Locked, got {result}");
        Assert.False(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterLock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        Assert.Equal(AccountStatus.Locked, sessionAfterLock.Status);
    }

    [Fact]
    public async Task Handle_AlreadyLockedAccount_StaysLockedAndDoesNotThrow()
    {
        var (accountId, _, username) = await CreateAccountAsync();
        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(result is LockAccountResult.Locked, $"Expected Locked, got {result}");
        Assert.False(await _keycloak.GetUserEnabledAsync(username));
    }

    [Fact]
    public async Task Handle_UnknownAccount_ReturnsAccountNotFound()
    {
        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(new AccountId(Guid.NewGuid())));

        Assert.True(result is LockAccountResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_LockedAccount_UnlockRestoresActiveAndReEnablesTheKeycloakUser()
    {
        var (accountId, bohemiaId, username) = await CreateAccountAsync();
        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        var result = await Send<UnlockAccountCommand, UnlockAccountResult>(new UnlockAccountCommand(accountId));

        Assert.True(result is UnlockAccountResult.Unlocked, $"Expected Unlocked, got {result}");
        Assert.True(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterUnlock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        Assert.Equal(AccountStatus.Active, sessionAfterUnlock.Status);
    }

    private async Task<TResponse> Send<TCommand, TResponse>(TCommand command) where TCommand : IRequest<TResponse>
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
```

- [ ] **Step 2: Confirm it fails to build**

Run: `dotnet build tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj`
Expected: FAIL — `CS0246: The type or namespace name 'LockAccountCommand' could not be found`.

- [ ] **Step 3: Add `Append` to the repository interface and implementation**

In `src/Accounts/Accounts.Application/Common/IAccountRepository.cs`:

```csharp
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IAccountRepository
{
    ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<Account?> FindByBohemiaIdAsync(GameId bohemiaId, CancellationToken cancellationToken);

    void StartStream(Account account, AccountCreated @event);

    void Append<TEvent>(AccountId accountId, TEvent @event) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
```

In `src/Accounts/Accounts.Infrastructure/Common/MartenAccountRepository.cs`, add after `StartStream`:

```csharp
    public void Append<TEvent>(AccountId accountId, TEvent @event) where TEvent : notnull
        => session.Events.Append(accountId.Value, @event);
```

- [ ] **Step 4: Create `LockAccountCommand`**

Create `src/Accounts/Accounts.Application/Accounts/LockAccountCommand.cs`:

```csharp
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union LockAccountResult(LockAccountResult.Locked, LockAccountResult.AccountNotFound)
{
    public record Locked;

    public record AccountNotFound;
}

public sealed record LockAccountCommand(AccountId AccountId) : IRequest<LockAccountResult>;

public sealed class LockAccountHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<LockAccountCommand, LockAccountResult>
{
    public async ValueTask<LockAccountResult> Handle(LockAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new LockAccountResult.AccountNotFound();
        }

        if (account.Status == AccountStatus.Active)
        {
            var @event = account.Lock();
            accountRepository.Append(request.AccountId, @event);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        // Disabling on every call (not just on the first lock) makes this safe to retry if a
        // previous lock's Keycloak call failed after the domain state already committed.
        await keycloakUserProvisioner.DisableUserAsync(account.KeycloakUserId, cancellationToken);

        return new LockAccountResult.Locked();
    }
}
```

- [ ] **Step 5: Create `UnlockAccountCommand`**

Create `src/Accounts/Accounts.Application/Accounts/UnlockAccountCommand.cs`:

```csharp
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union UnlockAccountResult(UnlockAccountResult.Unlocked, UnlockAccountResult.AccountNotFound)
{
    public record Unlocked;

    public record AccountNotFound;
}

public sealed record UnlockAccountCommand(AccountId AccountId) : IRequest<UnlockAccountResult>;

public sealed class UnlockAccountHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<UnlockAccountCommand, UnlockAccountResult>
{
    public async ValueTask<UnlockAccountResult> Handle(UnlockAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new UnlockAccountResult.AccountNotFound();
        }

        if (account.Status == AccountStatus.Locked)
        {
            var @event = account.Unlock();
            accountRepository.Append(request.AccountId, @event);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        await keycloakUserProvisioner.EnableUserAsync(account.KeycloakUserId, cancellationToken);

        return new UnlockAccountResult.Unlocked();
    }
}
```

- [ ] **Step 6: Build and run the tests (requires local infra)**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded.

Run: `dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj --filter LockAccountCommandTests`
Expected: PASS (4 tests). If local infra isn't available, skip and note it — the build in this step is the required gate.

- [ ] **Step 7: Commit**

```bash
git add src/Accounts/Accounts.Application/Common/IAccountRepository.cs \
        src/Accounts/Accounts.Infrastructure/Common/MartenAccountRepository.cs \
        src/Accounts/Accounts.Application/Accounts/LockAccountCommand.cs \
        src/Accounts/Accounts.Application/Accounts/UnlockAccountCommand.cs \
        tests/Accounts.IntegrationTests/LockAccountCommandTests.cs
git commit -m "feat(accounts): add LockAccountCommand/UnlockAccountCommand"
```

---

### Task 4: Admin lock/unlock HTTP endpoints + Keycloak scope/client

**Files:**
- Modify: `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`
- Modify: `infra/keycloak/eliferpg-realm.json`

**Interfaces:**
- Consumes: `LockAccountCommand`/`LockAccountResult`, `UnlockAccountCommand`/`UnlockAccountResult` (Task 3).
- Produces: `POST api/accounts/{accountId}/lock`, `POST api/accounts/{accountId}/unlock`, gated by a new `Accounts.Manage` policy requiring the `accounts:manage` scope. `AccountModule.AccountsManageScope` constant for other code/docs to reference.

- [ ] **Step 1: Add the new scope and a `staff-admin-dev` client to the Keycloak realm export**

In `infra/keycloak/eliferpg-realm.json`, add to the `clientScopes` array (alongside the existing `gameserver:*` entries):

```json
    {
      "name": "accounts:manage",
      "protocol": "openid-connect",
      "attributes": {
        "include.in.token.scope": "true",
        "display.on.consent.screen": "false"
      }
    },
```

Add to the `clients` array (a Client Credentials client, deliberately without `standard.token.exchange.enabled` or the `impersonation` role — this client only calls the Central API, never Keycloak's token-exchange endpoint):

```json
    {
      "clientId": "staff-admin-dev",
      "surrogateAuthRequired": false,
      "enabled": true,
      "alwaysDisplayInConsole": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "staff-secret-change-me",
      "redirectUris": [],
      "webOrigins": [],
      "notBefore": 0,
      "bearerOnly": false,
      "consentRequired": false,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "publicClient": false,
      "frontchannelLogout": false,
      "protocol": "openid-connect",
      "attributes": {
        "realm_client": "false",
        "backchannel.logout.session.required": "true",
        "backchannel.logout.revoke.offline.tokens": "false"
      },
      "authenticationFlowBindingOverrides": {},
      "fullScopeAllowed": true,
      "nodeReRegistrationTimeout": -1,
      "defaultClientScopes": [
        "web-origins",
        "acr",
        "profile",
        "roles",
        "accounts:manage",
        "basic",
        "email"
      ],
      "optionalClientScopes": [
        "address",
        "phone",
        "organization",
        "offline_access",
        "microprofile-jwt"
      ]
    },
```

- [ ] **Step 2: Restart the local Keycloak container to pick up the realm change and verify the new client can mint a scoped token**

Run: `docker compose up -d --force-recreate keycloak`

Then, once Keycloak is healthy:

```sh
curl -s -X POST http://localhost:8180/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['scope'])"
```

Expected: output includes `accounts:manage`.

- [ ] **Step 3: Add the endpoints and policy**

In `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`, update the class:

```csharp
using ELifeRPG.Accounts.Api.Sessions;
using ELifeRPG.Accounts.Application.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class AccountModule
{
    public const string SessionCreateScope = "gameserver:session:create";
    public const string AccountsManageScope = "accounts:manage";
    private const string SessionCreatePolicy = "Accounts.SessionCreate";
    private const string AccountsManagePolicy = "Accounts.Manage";

    public static IServiceCollection AddAccountModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAccountInfrastructure(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(SessionCreatePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(SessionCreateScope)))
            .AddPolicy(AccountsManagePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(AccountsManageScope)));

        return services;
    }

    public static WebApplication MapAccountModule(this WebApplication app)
    {
        var group = app.MapGroup("api/accounts").WithTags("Accounts");

        group.MapPost("session-bootstrap", async (
                [FromBody] CreateSessionRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(SessionDto.Create(result));
            })
            .RequireAuthorization(SessionCreatePolicy)
            .Produces<SessionDto>()
            .WithName("BootstrapSession")
            .WithDescription("Bootstraps (or looks up) a session for a player's Bohemia ID, provisioning an account if needed. Always returns 200 — a blocked account is reported via the Status field, not an error.");

        group.MapPost("{accountId:guid}/lock", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new LockAccountCommand(new AccountId(accountId)), cancellationToken);

                return result switch
                {
                    LockAccountResult.Locked => Results.NoContent(),
                    LockAccountResult.AccountNotFound => Results.Problem(
                        title: "Account not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(AccountsManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("LockAccount")
            .WithDescription("Locks (blocks) an account: disables its Keycloak user (so no new player token can ever be issued) and marks it Locked. Idempotent — locking an already-locked account still returns 204.");

        group.MapPost("{accountId:guid}/unlock", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new UnlockAccountCommand(new AccountId(accountId)), cancellationToken);

                return result switch
                {
                    UnlockAccountResult.Unlocked => Results.NoContent(),
                    UnlockAccountResult.AccountNotFound => Results.Problem(
                        title: "Account not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(AccountsManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UnlockAccount")
            .WithDescription("Unlocks an account: re-enables its Keycloak user and marks it Active. Idempotent — unlocking an already-active account still returns 204.");

        return app;
    }
}
```

- [ ] **Step 4: Build, run `src/Api`, and verify end-to-end with curl (requires local infra)**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded.

With `src/Api` running (`dotnet run --project src/Api/Api.csproj`) and infra up:

```sh
STAFF_TOKEN=$(curl -s -X POST http://localhost:8180/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

# Bootstrap an account first (reuse $BRIDGE_TOKEN from docs/accounts.md), grab its accountId, then:
curl -i -X POST "http://localhost:5100/api/accounts/$ACCOUNT_ID/lock" -H "Authorization: Bearer $STAFF_TOKEN"
# Expected: 204 No Content

curl -X POST http://localhost:5100/api/accounts/session-bootstrap \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"bohemiaId\":\"$BOHEMIA_ID\"}"
# Expected: 200, body contains "status":"blocked"

curl -i -X POST "http://localhost:5100/api/accounts/$ACCOUNT_ID/unlock" -H "Authorization: Bearer $STAFF_TOKEN"
# Expected: 204 No Content
```

If local infra isn't available, skip this step and note it — Step 4's build is the required gate.

- [ ] **Step 5: Commit**

```bash
git add src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs infra/keycloak/eliferpg-realm.json
git commit -m "feat(accounts): add admin lock/unlock endpoints and accounts:manage scope"
```

---

### Task 5: Bridge reflects blocked status; fixes error-translation gap

**Files:**
- Regenerate: `src/Bridge/Bridge.ApiClient/Generated/**` (via `scripts/generate-bridge-client.sh`, never hand-edited)
- Modify: `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`

**Interfaces:**
- Consumes: regenerated `ELifeRPG.Bridge.ApiClient.Models.SessionDto.Status` (nullable `string?`, per Kiota's existing convention for every other field on this model — see the pre-existing `session.KeycloakUsername!`/`session.AccountId!.Value` null-forgiving usages).
- Produces: `PlayerConnectedResponse(Guid AccountId, string Status, string? PlayerAccessToken, int? ExpiresInSeconds)` — the `Status`/nullable-token shape change is a breaking contract change for any existing mod-side caller, which is expected and intentional per the spec.

- [ ] **Step 1: Regenerate the Bridge's Kiota client**

Run (from inside the devcontainer, per `docs/bridge.md`):

```sh
dotnet tool restore
bash scripts/generate-bridge-client.sh
```

Expected: script completes, `git status` shows changes under `openapi/eliferpg-api-v1.json` and `src/Bridge/Bridge.ApiClient/Generated/` reflecting the new `SessionDto.Status` field and the two new `lock`/`unlock` operations.

- [ ] **Step 2: Update `SessionLocalEndpoints.cs`**

Replace `src/Bridge/Bridge.Host/SessionLocalEndpoints.cs` in full:

```csharp
using ELifeRPG.Bridge.ApiClient;
using ApiModels = ELifeRPG.Bridge.ApiClient.Models;

namespace ELifeRPG.Bridge.Host;

public static class SessionLocalEndpoints
{
    public static WebApplication MapSessionLocalEndpoints(this WebApplication app)
    {
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
                catch (ApiModels.ProblemDetails problem)
                {
                    return Results.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.ResponseStatusCode);
                }

                if (session is null)
                {
                    return Results.Problem("Central API returned an empty session response.");
                }

                if (session.Status == "blocked")
                {
                    return Results.Ok(new PlayerConnectedResponse(session.AccountId!.Value, session.Status!, null, null));
                }

                var playerToken = await tokenProvider.ExchangeForPlayerTokenAsync(session.KeycloakUsername!, cancellationToken);
                sessions.Start(request.BohemiaId, session.AccountId!.Value);

                return Results.Ok(new PlayerConnectedResponse(
                    session.AccountId!.Value,
                    session.Status!,
                    playerToken.AccessToken,
                    playerToken.ExpiresInSeconds));
            })
            .WithName("PlayerConnected")
            .WithDescription("Local-only: stands in for the mod's 'player connected' call until real Reforger integration lands. A blocked account gets Status=\"blocked\" and no token — never a Bridge-local session.");

        app.MapPost("character-selected", async (
                CharacterSelectedRequest request,
                EliferpgApiClient apiClient,
                PlayerSessionTracker sessions,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await apiClient.Api.Characters[request.CharacterId].Sessions.PostAsync(cancellationToken: cancellationToken);
                }
                catch (ApiModels.ProblemDetails problem)
                {
                    return Results.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.ResponseStatusCode);
                }

                sessions.SetActiveCharacter(request.BohemiaId, request.CharacterId);

                return Results.Ok();
            })
            .WithName("CharacterSelected")
            .WithDescription("Local-only: starts the selected character's session in the Central API, e.g. when a player picks a character at the in-game character-select screen (a separate, later moment than player-connected).");

        app.MapPost("player-disconnected", async (
                PlayerDisconnectedRequest request,
                EliferpgApiClient apiClient,
                PlayerSessionTracker sessions,
                CancellationToken cancellationToken) =>
            {
                var session = sessions.End(request.BohemiaId);

                if (session?.ActiveCharacterId is { } characterId)
                {
                    try
                    {
                        await apiClient.Api.Characters[characterId].Sessions.DeleteAsync(cancellationToken: cancellationToken);
                    }
                    catch (ApiModels.ProblemDetails problem)
                    {
                        return Results.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.ResponseStatusCode);
                    }
                }

                return Results.Ok();
            })
            .WithName("PlayerDisconnected")
            .WithDescription("Local-only: ends the Bridge's local record of a player's connection, started by player-connected, and ends that player's currently-selected character's session in the Central API, if one was ever selected.");

        return app;
    }
}

public sealed record PlayerConnectedRequest(Guid BohemiaId);

public sealed record PlayerConnectedResponse(Guid AccountId, string Status, string? PlayerAccessToken, int? ExpiresInSeconds);

public sealed record PlayerDisconnectedRequest(Guid BohemiaId);

public sealed record CharacterSelectedRequest(Guid BohemiaId, Guid CharacterId);
```

- [ ] **Step 3: Build**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded.

- [ ] **Step 4: Manual end-to-end verification (requires local infra; no automated test project exists for `Bridge.Host` today)**

With `src/Api` and `src/Bridge/Bridge.Host` both running:

```sh
curl -s -X POST http://localhost:5200/player-connected -H "Content-Type: application/json" \
  -d '{"bohemiaId":"22222222-2222-2222-2222-222222222222"}'
# Expected: 200, {"accountId":"...","status":"active","playerAccessToken":"...","expiresInSeconds":300}

STAFF_TOKEN=$(curl -s -X POST http://localhost:8180/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
curl -X POST "http://localhost:5100/api/accounts/<accountId from above>/lock" -H "Authorization: Bearer $STAFF_TOKEN"

curl -s -X POST http://localhost:5200/player-connected -H "Content-Type: application/json" \
  -d '{"bohemiaId":"22222222-2222-2222-2222-222222222222"}'
# Expected: 200, {"accountId":"...","status":"blocked","playerAccessToken":null,"expiresInSeconds":null}
```

- [ ] **Step 5: Commit**

```bash
git add openapi/eliferpg-api-v1.json src/Bridge/Bridge.ApiClient/Generated src/Bridge/Bridge.Host/SessionLocalEndpoints.cs
git commit -m "feat(bridge): reflect blocked account status, no token; translate character-session errors"
```

---

### Task 6: Docs

**Files:**
- Modify: `docs/accounts.md`
- Modify: `docs/bridge.md`

**Interfaces:**
- Consumes: the finished endpoints/response shapes from Tasks 1, 4, and 5 — this task only documents them.

- [ ] **Step 1: Update `docs/accounts.md`**

After the existing `session-bootstrap` curl example (which now returns a `status` field — no text change needed there beyond noting the field), append:

```markdown

`session-bootstrap` always returns `200` — a blocked account comes back as `{"status": "blocked", ...}` with no error, rather than a `403`.

## Locking and unlocking an account

`POST /api/accounts/{accountId}/lock` and `POST /api/accounts/{accountId}/unlock` require a bearer token with the `accounts:manage` scope — deliberately not granted to `gameserver-dev`, since banning a player is an admin/staff action, not something the game server does. Get one from the pre-provisioned dev client:

```sh
STAFF_TOKEN=$(curl -s -X POST http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -i -X POST http://localhost:5100/api/accounts/$ACCOUNT_ID/lock -H "Authorization: Bearer $STAFF_TOKEN"
curl -i -X POST http://localhost:5100/api/accounts/$ACCOUNT_ID/unlock -H "Authorization: Bearer $STAFF_TOKEN"
```

Both are idempotent (locking an already-locked account, or unlocking an already-active one, still returns `204`) and return `404` for an unknown `accountId`. Locking also disables the account's Keycloak user, so no new player token can be issued until it's unlocked — see [ARCHITECTURE.md §4.3](../ARCHITECTURE.md#43-player-identity-token-exchange) for why disabling the user (not just revoking sessions) is what actually stops the Bridge's token-exchange flow. An already-issued player token is unaffected until it naturally expires (the realm's access token lifespan is 5 minutes).
```

- [ ] **Step 2: Update `docs/bridge.md`**

In the "Connection and session lifecycle" section, after the existing `player-connected` curl example and its explanatory paragraph, add:

```markdown

If the account is blocked, `player-connected` still returns `200`, but with `"status": "blocked"`, `"playerAccessToken": null`, and no Bridge-local session recorded — the Central API's token-exchange step is skipped entirely, so a blocked player never receives a token.
```

- [ ] **Step 3: Commit**

```bash
git add docs/accounts.md docs/bridge.md
git commit -m "docs: document account status, lock/unlock endpoints, and blocked player-connected shape"
```
