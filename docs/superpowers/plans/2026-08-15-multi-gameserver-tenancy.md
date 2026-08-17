# Multi-Gameserver Tenancy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Isolate `Character`, `Company`, `Bank`, and `BankAccount` data per gameserver within one tenant, so a character (and its bank accounts/company memberships) created via one ArmA Reforger gameserver instance is invisible to every other gameserver in the same deployment.

**Architecture:** Turn on Marten's built-in conjoined multi-tenancy for the `Characters`, `Banking`, and `Companies` Marten stores, keyed by the calling gameserver's OAuth `client_id` (the same claim `session-bootstrap` already trusts). A new per-module `ICurrentGameServer` abstraction resolves that value; each module's Marten repository opens its session via `store.LightweightSession(currentGameServer.ClientId)` instead of the bare overload. `Account` and `GameServer` are untouched — they stay tenant-wide by design.

**Tech Stack:** .NET 11 preview, Marten 9.23.0 (event sourcing + document store, conjoined multi-tenancy), ASP.NET Core minimal APIs, Mediator (Mediator.SourceGenerator), xUnit integration tests against live Postgres/Keycloak.

**Spec:** [docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md](../specs/2026-08-15-multi-gameserver-tenancy-design.md)

## Global Constraints

- `ICurrentGameServer` is defined **once per module** (`Characters.Application.Common`, `Banking.Application.Common`, `Companies.Application.Common`) — not shared via `Shared.Kernel` or any cross-module reference. See spec's "New abstraction" section for why.
- **Refinement over the spec's literal wording:** the spec says the production `ICurrentGameServer` implementation lives in each module's `Infrastructure.Common`. Grounding this against the actual project files changes that: `*.Infrastructure.csproj` projects only reference the `Marten` package today (no ASP.NET Core dependency, deliberately lean), while `*.Api.csproj` projects already carry `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. So the production implementation (`HttpContextCurrentGameServer`, which needs `IHttpContextAccessor`) is placed in each module's `*.Api/Common` folder instead, registered from that module's `AddXModule(...)` (not `AddXInfrastructure(...)`). This adds no new package dependencies anywhere and matches the codebase's existing convention of reading JWT claims at the Api layer (`RealmRoleAuthorization`, `AccountEndpoints.cs`'s `user.FindFirst("client_id")`). Functionally identical to the spec; only the file's home project changes.
- No changes to any `Application` layer command/query signatures in `Characters`, `Banking`, or `Companies` — tenancy is transparent once each repository opens the right session.
- `Account`, `GameServer`, `WhitelistApplication` (all in `Accounts`) are explicitly out of scope — do not touch their Marten config.
- Every new/modified integration test file keeps the existing "Requires the local infra stack (`docker compose up -d`)..." doc comment convention already on every `IAsyncLifetime` test class in this repo.
- Local dev only — no production deployment exists yet, so schema resets (`DROP SCHEMA ... CASCADE`) are an acceptable, expected part of rolling this out locally. Do not add any data-migration code.

---

## File Structure

New files (three near-identical sets, one per module):

```
src/Characters/Characters.Application/Common/ICurrentGameServer.cs      (new)
src/Characters/Characters.Api/Common/HttpContextCurrentGameServer.cs    (new)
src/Banking/Banking.Application/Common/ICurrentGameServer.cs            (new)
src/Banking/Banking.Api/Common/HttpContextCurrentGameServer.cs          (new)
src/Companies/Companies.Application/Common/ICurrentGameServer.cs        (new)
src/Companies/Companies.Api/Common/HttpContextCurrentGameServer.cs      (new)
tests/Characters.IntegrationTests/TestServices.cs                       (new)
tests/Companies.IntegrationTests/TestServices.cs                        (new)
```

Modified files:

```
src/Api/Program.cs                                                              (add AddHttpContextAccessor)
src/Characters/Characters.Api/Characters/CharacterEndpoints.cs                  (register ICurrentGameServer)
src/Characters/Characters.Infrastructure/ServiceCollectionExtensions.cs         (enable tenancy)
src/Characters/Characters.Infrastructure/Common/MartenCharacterRepository.cs    (tenant-scoped session)
src/Banking/Banking.Api/BankingEndpoints.cs                                     (register ICurrentGameServer)
src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs               (enable tenancy)
src/Banking/Banking.Infrastructure/Common/MartenBankRepository.cs               (tenant-scoped session)
src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs        (tenant-scoped session)
src/Companies/Companies.Api/CompanyEndpoints.cs                                (register ICurrentGameServer)
src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs           (enable tenancy)
src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs        (tenant-scoped session)
tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs                (use TestServices, add isolation test)
tests/Banking.IntegrationTests/TestServices.cs                                  (parameterize BuildProvider)
tests/Banking.IntegrationTests/BankingCommandTests.cs                           (add isolation test)
tests/Companies.IntegrationTests/CompanyCommandTests.cs                         (use TestServices, add isolation test)
docs/ARCHITECTURE.md                                                            (cross-reference note, §4.1)
README.md                                                                       (schema reset note)
```

---

### Task 1: Characters — tenant-scoped sessions + host wiring

**Files:**
- Create: `src/Characters/Characters.Application/Common/ICurrentGameServer.cs`
- Create: `src/Characters/Characters.Api/Common/HttpContextCurrentGameServer.cs`
- Create: `tests/Characters.IntegrationTests/TestServices.cs`
- Modify: `src/Api/Program.cs`
- Modify: `src/Characters/Characters.Api/Characters/CharacterEndpoints.cs`
- Modify: `src/Characters/Characters.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/Characters/Characters.Infrastructure/Common/MartenCharacterRepository.cs`
- Modify: `tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs`
- Test: `tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs`

**Interfaces:**
- Produces: `ELifeRPG.Characters.Application.Common.ICurrentGameServer { string ClientId { get; } }` — consumed by `MartenCharacterRepository` in this task, and referenced as the pattern to copy in Tasks 2 and 3 (each module gets its own copy of this interface, not a shared one).
- Produces: `ELifeRPG.Characters.IntegrationTests.TestServices.BuildProvider(string gameServerClientId = "gameserver-dev") : ServiceProvider` — the pattern Task 3 copies for `Companies.IntegrationTests`.

- [ ] **Step 1: Write the new interface, its test double, and the failing isolation test**

Create `tests/Characters.IntegrationTests/TestServices.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup — see Banking.IntegrationTests/TestServices.cs for why this needs to be
/// the single AddMediator(...) call site for this compiled test project (Mediator.SourceGenerator
/// rejects a second one). Parameterized by the gameserver client id so tests can build two
/// independently-tenanted providers to prove per-server isolation.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CharacterDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
                ["Keycloak:Realm"] = "eliferpg",
                ["Keycloak:ProvisioningClientId"] = "account-service",
                ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies =
            [
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(
            _ => new FixedCurrentGameServer(gameServerClientId));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) : ELifeRPG.Characters.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
```

Modify `tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs`: replace the entire body of `InitializeAsync` (the `ConfigurationBuilder`/`ServiceCollection` block, lines 26–52 of the current file) with:

```csharp
public Task InitializeAsync()
{
    _provider = TestServices.BuildProvider();
    return Task.CompletedTask;
}
```

Then append this new test method to the same class, right after `Handle_CharactersQuery_ReturnsCreatedCharactersForAccount`:

```csharp
[Fact]
public async Task Handle_CharacterCreatedUnderOneServer_IsInvisibleFromAnotherServer()
{
    await using var providerB = TestServices.BuildProvider("gameserver-two");

    // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
    // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
    await using var scopeA = _provider.CreateAsyncScope();
    var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
    var accountId = await CreateActiveAccountAsync(mediatorA);

    var created = await mediatorA.Send(new CreateCharacterCommand(accountId, "Server A Character"));
    Assert.True(created is CreateCharacterResult.Created, $"Expected Created, got {created}");
    if (created is not CreateCharacterResult.Created createdCharacter)
    {
        throw new InvalidOperationException("Unreachable.");
    }

    await using var scopeB = providerB.CreateAsyncScope();
    var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

    var lookupFromCreatingServer = await mediatorA.Send(new CharacterLookupQuery(createdCharacter.CharacterId));
    var lookupFromOtherServer = await mediatorB.Send(new CharacterLookupQuery(createdCharacter.CharacterId));

    Assert.True(lookupFromCreatingServer is CharacterLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
    Assert.True(lookupFromOtherServer is CharacterLookupResult.NotFound, $"Expected NotFound from a different server, got {lookupFromOtherServer}");
}
```

- [ ] **Step 2: Confirm it fails to build**

Run: `dotnet build tests/Characters.IntegrationTests/Characters.IntegrationTests.csproj`
Expected: FAIL — `ELifeRPG.Characters.Application.Common.ICurrentGameServer` does not exist yet, so `TestServices.cs` won't compile.

- [ ] **Step 3: Add the `ICurrentGameServer` interface**

Create `src/Characters/Characters.Application/Common/ICurrentGameServer.cs`:

```csharp
namespace ELifeRPG.Characters.Application.Common;

/// <summary>
/// The gameserver whose data the current request should be scoped to — resolves to the calling
/// Bridge's own OAuth client id. Every session this module opens is scoped to this value, so a
/// character (and anything hanging off it) created via one gameserver is invisible from another,
/// even within the same tenant. See
/// docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md.
/// </summary>
public interface ICurrentGameServer
{
    string ClientId { get; }
}
```

- [ ] **Step 4: Add the production implementation and wire it into the module**

Create `src/Characters/Characters.Api/Common/HttpContextCurrentGameServer.cs`:

```csharp
using ELifeRPG.Characters.Application.Common;
using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Characters.Api.Common;

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
```

In `src/Characters/Characters.Api/Characters/CharacterEndpoints.cs`, add two usings at the top:

```csharp
using ELifeRPG.Characters.Api.Common;
using ELifeRPG.Characters.Application.Common;
```

Then in `AddCharacterModule`, add one line right after `services.AddCharacterInfrastructure(configuration);`:

```csharp
    public static IServiceCollection AddCharacterModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCharacterInfrastructure(configuration);
        services.AddScoped<ICurrentGameServer, HttpContextCurrentGameServer>();

        services.AddAuthorizationBuilder()
```

In `src/Api/Program.cs`, add one line right before `builder.Services.AddAuthorization();`:

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization();
```

- [ ] **Step 5: Make `MartenCharacterRepository` open a tenant-scoped session**

Modify `src/Characters/Characters.Infrastructure/Common/MartenCharacterRepository.cs` — change the constructor:

```csharp
    public MartenCharacterRepository(ICharactersStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }
```

(`ICurrentGameServer` is already reachable here — the file already has `using ELifeRPG.Characters.Application.Common;`.)

- [ ] **Step 6: Enable conjoined tenancy for the `Characters` store**

Modify `src/Characters/Characters.Infrastructure/ServiceCollectionExtensions.cs`. Add `using ELifeRPG.Characters.Domain;` to the usings, then update the `AddMartenStore<ICharactersStore>` call:

```csharp
        services.AddMartenStore<ICharactersStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("CharacterDatabase")!);
            options.Events.DatabaseSchemaName = "characters";
            options.DatabaseSchemaName = "characters";
            options.Events.TenancyStyle = JasperFx.Events.TenancyStyle.Conjoined;
            options.Schema.For<Character>().MultiTenanted();
            options.Projections.Add<CharacterProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });
```

- [ ] **Step 7: Reset the local `characters` schema and run the test project**

The store's shape just changed (tenant partitioning columns) — the existing local dev schema predates this, so it needs a clean slate. Run:

```bash
docker exec eliferpg-core-postgres-1 psql -U postgres -c "DROP SCHEMA IF EXISTS characters CASCADE;"
```

Then run:

```bash
dotnet test tests/Characters.IntegrationTests/Characters.IntegrationTests.csproj
```

Expected: all tests in the project PASS, including the new `Handle_CharacterCreatedUnderOneServer_IsInvisibleFromAnotherServer`.

- [ ] **Step 8: Commit**

```bash
git add src/Api/Program.cs \
  src/Characters/Characters.Application/Common/ICurrentGameServer.cs \
  src/Characters/Characters.Api/Common/HttpContextCurrentGameServer.cs \
  src/Characters/Characters.Api/Characters/CharacterEndpoints.cs \
  src/Characters/Characters.Infrastructure/ServiceCollectionExtensions.cs \
  src/Characters/Characters.Infrastructure/Common/MartenCharacterRepository.cs \
  tests/Characters.IntegrationTests/TestServices.cs \
  tests/Characters.IntegrationTests/CreateCharacterCommandTests.cs
git commit -m "feat(characters): scope character data to the calling gameserver"
```

---

### Task 2: Banking — tenant-scoped sessions for `Bank` and `BankAccount`

**Files:**
- Create: `src/Banking/Banking.Application/Common/ICurrentGameServer.cs`
- Create: `src/Banking/Banking.Api/Common/HttpContextCurrentGameServer.cs`
- Modify: `src/Banking/Banking.Api/BankingEndpoints.cs`
- Modify: `src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/Banking/Banking.Infrastructure/Common/MartenBankRepository.cs`
- Modify: `src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs`
- Modify: `tests/Banking.IntegrationTests/TestServices.cs`
- Modify: `tests/Banking.IntegrationTests/BankingCommandTests.cs`
- Test: `tests/Banking.IntegrationTests/BankingCommandTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 (this module's `ICurrentGameServer` is its own independent copy, per the Global Constraints).
- Produces: `ELifeRPG.Banking.Application.Common.ICurrentGameServer` — consumed by both `MartenBankRepository` and `MartenBankAccountRepository` in this task.

- [ ] **Step 1: Write the failing isolation test and parameterize `TestServices`**

Modify `tests/Banking.IntegrationTests/TestServices.cs` — change the method signature and add the fake registrations. Full new content:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class in this project. Mediator.SourceGenerator builds
/// one static dispatch table per compiled assembly from a single scan of AddMediator's
/// options.Assemblies configuration — having two separate `AddMediator(...)` call sites (e.g. one
/// per test class) fails to build with "MSG0007: Assemblies can only be configured once", even
/// though each call site listed the identical assembly list. Centralizing here keeps it to exactly
/// one call site regardless of how many test classes need a provider. See ARCHITECTURE.md §9e.
///
/// Parameterized by the gameserver client id so tests can build two independently-tenanted
/// providers to prove per-server isolation of Bank/BankAccount data.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CharacterDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:BankingDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CompanyDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
                ["Keycloak:Realm"] = "eliferpg",
                ["Keycloak:ProvisioningClientId"] = "account-service",
                ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies =
            [
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
                typeof(ELifeRPG.Banking.Application.AssemblyMarker),
                typeof(ELifeRPG.Companies.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddBankingInfrastructure(configuration);
        services.AddCompanyInfrastructure(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Banking.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Companies.Application.Common.ICurrentGameServer>(_ => fake);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer,
    ELifeRPG.Banking.Application.Common.ICurrentGameServer,
    ELifeRPG.Companies.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
```

Note: this compiles once `ICurrentGameServer` exists in all three modules — it won't build until Step 3 of this task (Banking's) and it also depends on Characters' copy already existing from Task 1, and Companies' copy not existing yet until Task 3. **This is expected to fail to build until Task 3 is also done** — see Step 2 below for what "fails" means at this point in the plan.

Append this test to `tests/Banking.IntegrationTests/BankingCommandTests.cs`, right after `TransactionHistory_TargetAccountSeesTransferredIn`:

```csharp
[Fact]
public async Task OpenBankAccount_ForBankOpenedOnAnotherServer_ReturnsBankNotFound()
{
    await using var providerB = TestServices.BuildProvider("gameserver-two");

    await using var scopeA = _provider.CreateAsyncScope();
    var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
    var bankId = await OpenBankAsync(mediatorA);

    await using var scopeB = providerB.CreateAsyncScope();
    var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();
    var characterOnServerB = await CreateCharacterAsync(mediatorB);

    var result = await mediatorB.Send(new OpenBankAccountCommand(bankId, characterOnServerB));

    Assert.True(result is OpenBankAccountResult.BankNotFound, $"Expected BankNotFound, got {result}");
}
```

The existing private `CreateCharacterAsync(IMediator mediator)` helper already takes the mediator as a parameter, so it works unchanged against `mediatorB`'s scope — no new helper needed.

- [ ] **Step 2: Confirm the Banking test project fails to build**

Run: `dotnet build tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj`
Expected: FAIL — `ELifeRPG.Banking.Application.Common.ICurrentGameServer` and `ELifeRPG.Companies.Application.Common.ICurrentGameServer` don't exist yet (Companies' copy lands in Task 3). This step exists to record the expected-red state; proceed to implement Banking's half now, then return to green once Task 3 lands.

- [ ] **Step 3: Add the `ICurrentGameServer` interface and production implementation for Banking**

Create `src/Banking/Banking.Application/Common/ICurrentGameServer.cs`:

```csharp
namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// The gameserver whose data the current request should be scoped to — resolves to the calling
/// Bridge's own OAuth client id. Every session this module opens is scoped to this value, so a
/// Bank/BankAccount opened via one gameserver is invisible from another, even within the same
/// tenant. See docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md.
/// </summary>
public interface ICurrentGameServer
{
    string ClientId { get; }
}
```

Create `src/Banking/Banking.Api/Common/HttpContextCurrentGameServer.cs`:

```csharp
using ELifeRPG.Banking.Application.Common;
using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Banking.Api.Common;

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
```

In `src/Banking/Banking.Api/BankingEndpoints.cs`, add two usings at the top:

```csharp
using ELifeRPG.Banking.Api.Common;
using ELifeRPG.Banking.Application.Common;
```

In `AddBankingModule` in that same file, add one line right after `services.AddBankingInfrastructure(configuration);`:

```csharp
services.AddBankingInfrastructure(configuration);
services.AddScoped<ICurrentGameServer, HttpContextCurrentGameServer>();
```

- [ ] **Step 4: Make `MartenBankRepository` and `MartenBankAccountRepository` open tenant-scoped sessions**

Modify `src/Banking/Banking.Infrastructure/Common/MartenBankRepository.cs` — add `using ELifeRPG.Banking.Application.Common;` to the usings, then change the constructor:

```csharp
    public MartenBankRepository(IBankingStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }
```

Modify `src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs` — add `using ELifeRPG.Banking.Application.Common;` to the usings, then change the constructor:

```csharp
    public MartenBankAccountRepository(IBankingStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }
```

- [ ] **Step 5: Enable conjoined tenancy for the `Banking` store**

Modify `src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs`. Add `using ELifeRPG.Banking.Domain;` to the usings, then update the `AddMartenStore<IBankingStore>` call:

```csharp
        services.AddMartenStore<IBankingStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("BankingDatabase")!);
            options.Events.DatabaseSchemaName = "banking";
            options.DatabaseSchemaName = "banking";
            options.Events.TenancyStyle = JasperFx.Events.TenancyStyle.Conjoined;
            options.Schema.For<Bank>().MultiTenanted();
            options.Schema.For<BankAccount>().MultiTenanted();
            options.Projections.Add<BankProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
            options.Projections.Add<BankAccountProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });
```

- [ ] **Step 6: Confirm Task 1 has landed, then reset the local `banking` schema and run both integration test projects**

This task's `TestServices.cs` referenced Companies' `ICurrentGameServer` in Step 1 above — that only exists after Task 3. If Task 3 hasn't landed yet, stop here and come back to this step once it has (or, if executing tasks out of order isn't intended, do Task 3 before this step). Once all three modules' `ICurrentGameServer` interfaces exist:

```bash
docker exec eliferpg-core-postgres-1 psql -U postgres -c "DROP SCHEMA IF EXISTS banking CASCADE;"
```

```bash
dotnet test tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj
```

Expected: all tests PASS, including the new `OpenBankAccount_ForBankOpenedOnAnotherServer_ReturnsBankNotFound` and every `CorporateBankAccountTests` test (that class shares this same `TestServices.BuildProvider()`, still called with no arguments so it keeps defaulting to `"gameserver-dev"` — no behavior change for it).

- [ ] **Step 7: Commit**

```bash
git add src/Banking/Banking.Application/Common/ICurrentGameServer.cs \
  src/Banking/Banking.Api/Common/HttpContextCurrentGameServer.cs \
  src/Banking/Banking.Api/BankingEndpoints.cs \
  src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs \
  src/Banking/Banking.Infrastructure/Common/MartenBankRepository.cs \
  src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs \
  tests/Banking.IntegrationTests/TestServices.cs \
  tests/Banking.IntegrationTests/BankingCommandTests.cs
git commit -m "feat(banking): scope bank and bank account data to the calling gameserver"
```

---

### Task 3: Companies — tenant-scoped sessions for `Company`

**Files:**
- Create: `src/Companies/Companies.Application/Common/ICurrentGameServer.cs`
- Create: `src/Companies/Companies.Api/Common/HttpContextCurrentGameServer.cs`
- Create: `tests/Companies.IntegrationTests/TestServices.cs`
- Modify: `src/Companies/Companies.Api/CompanyEndpoints.cs` (defines `AddCompanyModule`, mirrors `CharacterEndpoints.cs`/`BankingEndpoints.cs`'s shape)
- Modify: `src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs`
- Modify: `tests/Companies.IntegrationTests/CompanyCommandTests.cs`
- Test: `tests/Companies.IntegrationTests/CompanyCommandTests.cs`

**Interfaces:**
- Produces: `ELifeRPG.Companies.Application.Common.ICurrentGameServer` — this is the interface `tests/Banking.IntegrationTests/TestServices.cs` (Task 2) already references. Once this task lands, Task 2's test project builds clean.

- [ ] **Step 1: Write the isolation test and add `TestServices.cs`**

Create `tests/Companies.IntegrationTests/TestServices.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Companies.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup — see Banking.IntegrationTests/TestServices.cs for why this needs to be
/// the single AddMediator(...) call site for this compiled test project. Parameterized by the
/// gameserver client id so tests can build two independently-tenanted providers to prove per-server
/// isolation of Company data.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CharacterDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CompanyDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
                ["Keycloak:Realm"] = "eliferpg",
                ["Keycloak:ProvisioningClientId"] = "account-service",
                ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies =
            [
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
                typeof(ELifeRPG.Companies.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddCompanyInfrastructure(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Companies.Application.Common.ICurrentGameServer>(_ => fake);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer,
    ELifeRPG.Companies.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
```

Modify `tests/Companies.IntegrationTests/CompanyCommandTests.cs`: replace the entire body of `InitializeAsync` (the `ConfigurationBuilder`/`ServiceCollection` block, lines 26–53 of the current file) with:

```csharp
public Task InitializeAsync()
{
    _provider = TestServices.BuildProvider();
    return Task.CompletedTask;
}
```

Then append this test right after `CompaniesQuery_IncludesCreatedCompany`:

```csharp
[Fact]
public async Task Company_CreatedOnOneServer_IsInvisibleFromAnotherServer()
{
    await using var providerB = TestServices.BuildProvider("gameserver-two");

    await using var scopeA = _provider.CreateAsyncScope();
    var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
    var companyId = await CreateCompanyAsync(mediatorA);

    await using var scopeB = providerB.CreateAsyncScope();
    var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

    var lookupFromCreatingServer = await mediatorA.Send(new CompanyLookupQuery(companyId));
    var lookupFromOtherServer = await mediatorB.Send(new CompanyLookupQuery(companyId));

    Assert.True(lookupFromCreatingServer is CompanyLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
    Assert.True(lookupFromOtherServer is CompanyLookupResult.NotFound, $"Expected NotFound from a different server, got {lookupFromOtherServer}");
}
```

- [ ] **Step 2: Confirm the Companies test project fails to build**

Run: `dotnet build tests/Companies.IntegrationTests/Companies.IntegrationTests.csproj`
Expected: FAIL — `ELifeRPG.Companies.Application.Common.ICurrentGameServer` doesn't exist yet.

- [ ] **Step 3: Add the `ICurrentGameServer` interface and production implementation for Companies**

Create `src/Companies/Companies.Application/Common/ICurrentGameServer.cs`:

```csharp
namespace ELifeRPG.Companies.Application.Common;

/// <summary>
/// The gameserver whose data the current request should be scoped to — resolves to the calling
/// Bridge's own OAuth client id. Every session this module opens is scoped to this value, so a
/// Company created via one gameserver is invisible from another, even within the same tenant. See
/// docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md.
/// </summary>
public interface ICurrentGameServer
{
    string ClientId { get; }
}
```

Create `src/Companies/Companies.Api/Common/HttpContextCurrentGameServer.cs`:

```csharp
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
```

In `src/Companies/Companies.Api/CompanyEndpoints.cs`, add two usings at the top:

```csharp
using ELifeRPG.Companies.Api.Common;
using ELifeRPG.Companies.Application.Common;
```

and one line right after its `services.AddCompanyInfrastructure(configuration);` call:

```csharp
services.AddCompanyInfrastructure(configuration);
services.AddScoped<ICurrentGameServer, HttpContextCurrentGameServer>();
```

- [ ] **Step 4: Make `MartenCompanyRepository` open a tenant-scoped session**

Modify `src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs` — add `using ELifeRPG.Companies.Application.Common;` to the usings, then change the constructor:

```csharp
    public MartenCompanyRepository(ICompaniesStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }
```

- [ ] **Step 5: Enable conjoined tenancy for the `Companies` store**

Modify `src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs`. Add `using ELifeRPG.Companies.Domain;` to the usings, then update the `AddMartenStore<ICompaniesStore>` call:

```csharp
        services.AddMartenStore<ICompaniesStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("CompanyDatabase")!);
            options.Events.DatabaseSchemaName = "companies";
            options.DatabaseSchemaName = "companies";
            options.Events.TenancyStyle = JasperFx.Events.TenancyStyle.Conjoined;
            options.Schema.For<Company>().MultiTenanted();
            options.Projections.Add<CompanyProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });
```

- [ ] **Step 6: Reset the local `companies` schema and run the test project**

```bash
docker exec eliferpg-core-postgres-1 psql -U postgres -c "DROP SCHEMA IF EXISTS companies CASCADE;"
```

```bash
dotnet test tests/Companies.IntegrationTests/Companies.IntegrationTests.csproj
```

Expected: all tests PASS, including the new `Company_CreatedOnOneServer_IsInvisibleFromAnotherServer`.

- [ ] **Step 7: Go back and finish Task 2's Step 6**

Now that this module's `ICurrentGameServer` exists, `tests/Banking.IntegrationTests/TestServices.cs` (written in Task 2) compiles. Reset the `banking` schema and run `dotnet test tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj` per Task 2 Step 6 if that hasn't been done yet, and confirm it now passes.

- [ ] **Step 8: Commit**

```bash
git add src/Companies/Companies.Application/Common/ICurrentGameServer.cs \
  src/Companies/Companies.Api/Common/HttpContextCurrentGameServer.cs \
  src/Companies/Companies.Api/CompanyEndpoints.cs \
  src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs \
  src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs \
  tests/Companies.IntegrationTests/TestServices.cs \
  tests/Companies.IntegrationTests/CompanyCommandTests.cs
git commit -m "feat(companies): scope company data to the calling gameserver"
```

---

### Task 4: Full-suite verification and docs

**Files:**
- Modify: `ARCHITECTURE.md` (§4.1)
- Modify: `README.md` ("Resetting local data" section)

**Interfaces:**
- Consumes: everything from Tasks 1–3. This task adds no new code.

- [ ] **Step 1: Run the entire solution's build and full test suite**

```bash
dotnet build ELifeRPG.Core.slnx
```

Expected: PASS, no warnings-as-errors.

```bash
dotnet test ELifeRPG.Core.slnx
```

Expected: all eight test projects PASS (unit tests were untouched by Tasks 1–3; every integration test project was already reset and re-verified per-task, this is the full-solution confirmation pass).

- [ ] **Step 2: Add the cross-reference note to `ARCHITECTURE.md` §4.1**

Find this sentence in `ARCHITECTURE.md` (currently around line 94):

```
**Tenancy:** one Keycloak realm per tenant, where a tenant is one self-hosted ELifeRPG deployment (its own gameserver fleet, Central API, Postgres, and Keycloak instance). A realm holds both that tenant's players and its staff/Admin UI accounts — see [§4.3](#43-player-identity-token-exchange) for why that's a safe boundary rather than a risk to split further.
```

Append a new sentence to it:

```
**Tenancy:** one Keycloak realm per tenant, where a tenant is one self-hosted ELifeRPG deployment (its own gameserver fleet, Central API, Postgres, and Keycloak instance). A realm holds both that tenant's players and its staff/Admin UI accounts — see [§4.3](#43-player-identity-token-exchange) for why that's a safe boundary rather than a risk to split further. Within one tenant, `Characters`/`Banking`/`Companies` data is further isolated per gameserver instance using a *different, narrower* sense of "tenant" — Marten's own conjoined multi-tenancy, scoped to one gameserver's OAuth client id, always inside this one deployment's single Postgres database. See `docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md` for the full design; don't conflate the two.
```

- [ ] **Step 3: Add a schema-reset note to `README.md`**

Find the "Resetting local data" section (currently showing `DROP SCHEMA IF EXISTS account CASCADE;` as the example). Add one sentence after that code block:

```
The same works for any other module's schema — `characters`, `banking`, `companies` — if you only need to reset one.
```

- [ ] **Step 4: Commit**

```bash
git add ARCHITECTURE.md README.md
git commit -m "docs: cross-reference gameserver tenancy design in ARCHITECTURE.md and README"
```

---

## Self-Review Notes

- **Spec coverage:** every section of the spec has a corresponding task — `ICurrentGameServer` (Tasks 1–3, Step 3/4 each), repository session changes (Tasks 1–3), Marten tenancy config (Tasks 1–3), host wiring (Task 1), test doubles + isolation tests (Tasks 1–3), local dev rollout (schema-reset steps in each task), the `ARCHITECTURE.md` naming cross-reference (Task 4). The spec's "Out of scope" items (admin cross-server view, `GameServerId` value object, ProblemDetails mapping) are deliberately not tasked.
- **Ordering caveat, called out explicitly:** Tasks 1–3 have a real circular file dependency — `Banking.IntegrationTests/TestServices.cs` (Task 2) references Companies' `ICurrentGameServer` (Task 3), because Banking's existing shared `TestServices.BuildProvider()` already wires up all four modules together. Task 2 Step 6 and Task 3 Step 7 both call this out and tell the executor to complete Task 3 before Task 2's tests will pass. Tasks 1 and 3 have no such ordering dependency on each other; Task 1 has none on Task 2 or 3.
- **Type consistency:** `ICurrentGameServer.ClientId` (string), `store.LightweightSession(currentGameServer.ClientId)`, and `FixedCurrentGameServer(string clientId)` use the same type and name throughout every task.
