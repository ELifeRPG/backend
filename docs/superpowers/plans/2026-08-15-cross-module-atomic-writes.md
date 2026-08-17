# Cross-module atomic writes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let one operation atomically write to two modules' event streams — proven via a new `PurchaseCompanySharesCommand` that debits a `Banking.BankAccount` and credits `Companies.Company` shares in a single Postgres transaction, with no partial-success window.

**Architecture:** A new shared-transaction mechanism (`Shared.Integration`/`Shared.Integration.Abstractions`) opens one `NpgsqlConnection`/`NpgsqlTransaction` and lets each participating module bind a Marten session to it via `SessionOptions.ForTransaction(transaction, shouldAutoCommit: false)`. Each module exposes an explicitly-named repository factory from its own `Application` layer (`IBankAccountRepositoryFactory`, `ICompanyRepositoryFactory`); an orchestrating command in `Banking.Application` uses both factories, then commits the one underlying transaction once.

**Tech Stack:** .NET 11 (preview), Marten 9.23 (event sourcing on PostgreSQL), Npgsql, Mediator (source-generator based, not MediatR), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`

## Global Constraints

- Every new/modified `.csproj` follows this repo's existing pattern: no explicit `<TargetFramework>`/`<Nullable>`/`AssemblyName` (all inherited from root `Directory.Build.props`), and no `Version` on `PackageReference` (central package management via `Directory.Packages.props`, with `CentralPackageTransitivePinningEnabled=true`).
- **`Domain`, `Infrastructure`, and `Api` projects must never reference another module's `Domain`, `Infrastructure`, or `Api` project.** The only cross-module reference is `Application → Application`. This plan adds a second, narrow flavor of that exception: a module's `Application` layer may also expose an `I<X>RepositoryFactory` for another module's orchestrating command to call — never a `Domain`/`Infrastructure`/`Api` reference. See `ARCHITECTURE.md §9e`.
- Sessions bound to the shared cross-module transaction (via `SessionOptions.ForTransaction`) are never explicitly disposed by handler code — only `ICrossModuleTransaction` itself (via `await using`) owns and disposes the underlying `NpgsqlConnection`/`NpgsqlTransaction`. Do not add `IAsyncDisposable` to `IBankAccountRepository`/`ICompanyRepository` or call `DisposeAsync()` on repositories obtained from a `CreateFor(handle)` factory call — this avoids any risk of closing the shared connection before the transaction itself is done with it.
- All four existing module databases (`AccountDatabase`, `CharacterDatabase`, `BankingDatabase`, `CompanyDatabase`) point at the exact same physical Postgres instance/database (`Host=postgres;Database=postgres;...`, verified in `src/Api/appsettings.Development.json` and `tests/Banking.IntegrationTests/TestServices.cs`) — this is what makes a real shared transaction possible at all. The new `SharedDatabase` connection string added by this plan uses the identical value.
- Integration tests require the local infra stack running (`docker compose up -d`) and the devcontainer connected to its network — see `README.md`. They are not run as part of a plain `dotnet test` against an empty environment.

---

### Task 1: `Shared.Integration.Abstractions` project

**Files:**
- Create: `src/Shared/Shared.Integration.Abstractions/Shared.Integration.Abstractions.csproj`
- Create: `src/Shared/Shared.Integration.Abstractions/CrossModuleSessionHandle.cs`
- Create: `src/Shared/Shared.Integration.Abstractions/ICrossModuleTransaction.cs`
- Create: `src/Shared/Shared.Integration.Abstractions/ICrossModuleTransactionFactory.cs`

**Interfaces:**
- Produces: `ELifeRPG.Shared.Integration.Abstractions.CrossModuleSessionHandle` (opaque — no public members), `ICrossModuleTransaction { CrossModuleSessionHandle Handle; Task CommitAsync(CancellationToken); }` (also `IAsyncDisposable`), `ICrossModuleTransactionFactory { Task<ICrossModuleTransaction> BeginAsync(CancellationToken); }`.

This project deliberately has **no package references** (no Marten, no Npgsql) — it's the Application-layer-safe half of the mechanism, matching `Shared.Kernel`'s minimalism.

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
</Project>
```

- [ ] **Step 2: Add the opaque session handle**

`src/Shared/Shared.Integration.Abstractions/CrossModuleSessionHandle.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ELifeRPG.Shared.Integration")]

namespace ELifeRPG.Shared.Integration.Abstractions;

/// <summary>
/// Opaque handle to a shared cross-module database transaction. Application-layer code only ever
/// passes this through — the underlying transaction is reachable only via Shared.Integration's
/// public CrossModuleSessionHandleExtensions.Unwrap(), used exclusively by each participating
/// module's Infrastructure-layer repository factory. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public sealed class CrossModuleSessionHandle
{
    internal object RawTransaction { get; }

    internal CrossModuleSessionHandle(object rawTransaction)
    {
        RawTransaction = rawTransaction;
    }
}
```

- [ ] **Step 3: Add the transaction interfaces**

`src/Shared/Shared.Integration.Abstractions/ICrossModuleTransaction.cs`:

```csharp
namespace ELifeRPG.Shared.Integration.Abstractions;

/// <summary>
/// One shared database transaction spanning multiple modules' Marten sessions. Obtain module-scoped
/// repositories via each module's own "I&lt;X&gt;RepositoryFactory.CreateFor(Handle)", append/save
/// through them as normal, then call CommitAsync once. Disposing without committing rolls back —
/// there is no partial-success state. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface ICrossModuleTransaction : IAsyncDisposable
{
    CrossModuleSessionHandle Handle { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
```

`src/Shared/Shared.Integration.Abstractions/ICrossModuleTransactionFactory.cs`:

```csharp
namespace ELifeRPG.Shared.Integration.Abstractions;

public interface ICrossModuleTransactionFactory
{
    Task<ICrossModuleTransaction> BeginAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/Shared/Shared.Integration.Abstractions/Shared.Integration.Abstractions.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Shared.Integration.Abstractions
git commit -m "feat: add Shared.Integration.Abstractions project for cross-module transactions"
```

---

### Task 2: `Company.IssueShares` domain method

**Files:**
- Create: `src/Companies/Companies.Domain/Events/CompanySharesIssued.cs`
- Modify: `src/Companies/Companies.Domain/Company.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`
- Test: `tests/Companies.Domain.UnitTests/CompanyTests.cs`

**Interfaces:**
- Produces: `Company.Shares: List<CompanyShareGrant>` (new `[JsonInclude]` property), `Company.IssueShares(CharacterId buyer, int quantity) : CompanySharesIssued`, `Company.Apply(CompanySharesIssued)`, `CompanySharesIssued(CompanyId Id, CharacterId Buyer, int Quantity)`, `CompanyShareGrant(CharacterId CharacterId, int Quantity)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Companies.Domain.UnitTests/CompanyTests.cs` (inside the `CompanyTests` class, after the existing `AddMember_WithExplicitOwnerPosition_AssignsOwnerPosition` test):

```csharp
    [Fact]
    public void IssueShares_FirstPurchase_AddsShareGrant()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());

        var domainEvent = company.IssueShares(buyer, 10);

        Assert.Equal(10, domainEvent.Quantity);
        Assert.Contains(company.Shares, s => s.CharacterId == buyer && s.Quantity == 10);
    }

    [Fact]
    public void IssueShares_SecondPurchaseBySameBuyer_AccumulatesQuantity()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());
        company.IssueShares(buyer, 10);

        company.IssueShares(buyer, 5);

        var grant = Assert.Single(company.Shares, s => s.CharacterId == buyer);
        Assert.Equal(15, grant.Quantity);
    }

    [Fact]
    public void IssueShares_WithNonPositiveQuantity_Throws()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => company.IssueShares(buyer, 0));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~IssueShares"`
Expected: FAIL — `Company` has no member `IssueShares`, `Shares` (compile error).

- [ ] **Step 3: Add the event**

`src/Companies/Companies.Domain/Events/CompanySharesIssued.cs`:

```csharp
namespace ELifeRPG.Companies.Domain.Events;

public sealed record CompanySharesIssued(CompanyId Id, CharacterId Buyer, int Quantity);
```

- [ ] **Step 4: Add `CompanyShareGrant` and the domain method**

In `src/Companies/Companies.Domain/Company.cs`, add a new file-scoped record right after the `namespace ELifeRPG.Companies.Domain;` line (before `public class Company`):

```csharp
public sealed record CompanyShareGrant(CharacterId CharacterId, int Quantity);
```

Add a new `[JsonInclude]` property alongside the existing `Applications` property:

```csharp
    [JsonInclude]
    public List<CompanyShareGrant> Shares { get; private set; } = [];
```

Add the domain method after `DenyApplication`:

```csharp
    public CompanySharesIssued IssueShares(CharacterId buyer, int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        }

        var domainEvent = new CompanySharesIssued(Id, buyer, quantity);
        Apply(domainEvent);
        return domainEvent;
    }
```

Add the `Apply` overload after the existing `Apply(ApplicationDenied domainEvent)`:

```csharp
    public void Apply(CompanySharesIssued domainEvent)
    {
        var index = Shares.FindIndex(x => x.CharacterId == domainEvent.Buyer);
        if (index >= 0)
        {
            Shares[index] = Shares[index] with { Quantity = Shares[index].Quantity + domainEvent.Quantity };
        }
        else
        {
            Shares.Add(new CompanyShareGrant(domainEvent.Buyer, domainEvent.Quantity));
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~IssueShares"`
Expected: PASS — 3 tests passed.

- [ ] **Step 6: Wire the projection**

In `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`, add after the existing `Apply(Company company, ApplicationDenied domainEvent)` line:

```csharp
    public void Apply(Company company, CompanySharesIssued domainEvent) => company.Apply(domainEvent);
```

- [ ] **Step 7: Run the full Companies.Domain.UnitTests suite**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj`
Expected: PASS — all tests (existing + new) pass.

- [ ] **Step 8: Commit**

```bash
git add src/Companies/Companies.Domain src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs tests/Companies.Domain.UnitTests/CompanyTests.cs
git commit -m "feat: add Company.IssueShares domain method and CompanySharesIssued event"
```

---

### Task 3: `Shared.Integration` project (Marten/Npgsql mechanism)

**Files:**
- Create: `src/Shared/Shared.Integration/Shared.Integration.csproj`
- Create: `src/Shared/Shared.Integration/CrossModuleSessionHandleExtensions.cs`
- Create: `src/Shared/Shared.Integration/NpgsqlCrossModuleTransaction.cs`
- Create: `src/Shared/Shared.Integration/CrossModuleTransactionFactory.cs`
- Create: `src/Shared/Shared.Integration/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `CrossModuleSessionHandle`, `ICrossModuleTransaction`, `ICrossModuleTransactionFactory` (Task 1).
- Produces: `CrossModuleSessionHandleExtensions.Unwrap(this CrossModuleSessionHandle) : NpgsqlTransaction` (public — this is how module Infrastructure factories reach the real transaction), `IServiceCollection.AddCrossModuleIntegration(IConfiguration)`.

This project's correctness (does a real shared Postgres transaction actually make two modules' writes atomic) is verified end-to-end by Task 7's integration tests, not by an isolated test here — there's no meaningful behavior to unit-test in isolation from real Marten/Postgres.

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Marten" />
    <PackageReference Include="Npgsql" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Shared.Integration.Abstractions\Shared.Integration.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Build once to confirm the `Npgsql` package reference resolves**

Run: `dotnet build src/Shared/Shared.Integration/Shared.Integration.csproj`
Expected: Build succeeded (a project with zero `.cs` files still builds — this step only checks that restore succeeds and doesn't complain about a missing `PackageVersion` for `Npgsql`). If restore fails with a central-package-management error instead, add `<PackageVersion Include="Npgsql" Version="..." />` to `Directory.Packages.props` (pick the version NuGet reports Marten 9.23.0 already pulls in transitively) and retry.

- [ ] **Step 3: Add the unwrap extension**

`src/Shared/Shared.Integration/CrossModuleSessionHandleExtensions.cs`:

```csharp
using ELifeRPG.Shared.Integration.Abstractions;
using Npgsql;

namespace ELifeRPG.Shared.Integration;

public static class CrossModuleSessionHandleExtensions
{
    public static NpgsqlTransaction Unwrap(this CrossModuleSessionHandle handle) => (NpgsqlTransaction)handle.RawTransaction;
}
```

- [ ] **Step 4: Add the transaction implementation**

`src/Shared/Shared.Integration/NpgsqlCrossModuleTransaction.cs`:

```csharp
using ELifeRPG.Shared.Integration.Abstractions;
using Npgsql;

namespace ELifeRPG.Shared.Integration;

internal sealed class NpgsqlCrossModuleTransaction : ICrossModuleTransaction
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    private NpgsqlCrossModuleTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        Handle = new CrossModuleSessionHandle(transaction);
    }

    public static async Task<NpgsqlCrossModuleTransaction> BeginAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new NpgsqlCrossModuleTransaction(connection, transaction);
    }

    public CrossModuleSessionHandle Handle { get; }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            await _transaction.RollbackAsync();
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
```

- [ ] **Step 5: Add the factory and DI registration**

`src/Shared/Shared.Integration/CrossModuleTransactionFactory.cs`:

```csharp
using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Shared.Integration;

public sealed class CrossModuleTransactionFactory(string connectionString) : ICrossModuleTransactionFactory
{
    public async Task<ICrossModuleTransaction> BeginAsync(CancellationToken cancellationToken)
        => await NpgsqlCrossModuleTransaction.BeginAsync(connectionString, cancellationToken);
}
```

`src/Shared/Shared.Integration/ServiceCollectionExtensions.cs`:

```csharp
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Microsoft.Extensions.Configuration;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SharedIntegrationExtensions
{
    public static IServiceCollection AddCrossModuleIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SharedDatabase")!;
        services.AddSingleton<ICrossModuleTransactionFactory>(new CrossModuleTransactionFactory(connectionString));
        return services;
    }
}
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/Shared/Shared.Integration/Shared.Integration.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Shared/Shared.Integration
git commit -m "feat: add Shared.Integration project — shared-transaction mechanism"
```

---

### Task 4: Companies cross-module write support

**Files:**
- Modify: `src/Companies/Companies.Application/Companies.Application.csproj`
- Modify: `src/Companies/Companies.Infrastructure/Companies.Infrastructure.csproj`
- Create: `src/Companies/Companies.Application/Common/ICompanyRepositoryFactory.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs`
- Create: `src/Companies/Companies.Infrastructure/Common/MartenCompanyRepositoryFactory.cs`
- Modify: `src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `CrossModuleSessionHandle`, `CrossModuleSessionHandleExtensions.Unwrap` (Tasks 1, 3).
- Produces: `ICompanyRepositoryFactory { ICompanyRepository CreateFor(CrossModuleSessionHandle handle); }`, `MartenCompanyRepository(IDocumentSession session)` (new constructor overload).

- [ ] **Step 1: Add the project references**

In `src/Companies/Companies.Application/Companies.Application.csproj`, add to the existing `<ItemGroup>` with `ProjectReference`s:

```xml
    <!-- For ICompanyRepositoryFactory's CrossModuleSessionHandle parameter, used by other modules'
         orchestrating commands (e.g. Banking.Application.Companies.PurchaseCompanySharesCommand) —
         see ARCHITECTURE.md §9e. -->
    <ProjectReference Include="..\..\Shared\Shared.Integration.Abstractions\Shared.Integration.Abstractions.csproj" />
```

In `src/Companies/Companies.Infrastructure/Companies.Infrastructure.csproj`, add to the existing `<ItemGroup>` with `ProjectReference`s:

```xml
    <ProjectReference Include="..\..\Shared\Shared.Integration\Shared.Integration.csproj" />
```

- [ ] **Step 2: Add the factory interface**

`src/Companies/Companies.Application/Common/ICompanyRepositoryFactory.cs`:

```csharp
using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Companies.Application.Common;

/// <summary>
/// Builds a Companies repository bound to a shared cross-module transaction's session instead of
/// this module's normal per-request session — used only by orchestrating commands elsewhere (e.g.
/// Banking.Application.Companies.PurchaseCompanySharesCommand) that write to Companies and another
/// module atomically. See docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface ICompanyRepositoryFactory
{
    ICompanyRepository CreateFor(CrossModuleSessionHandle handle);
}
```

- [ ] **Step 3: Add the external-session constructor to `MartenCompanyRepository`**

In `src/Companies/Companies.Infrastructure/Common/MartenCompanyRepository.cs`, add a second constructor right after the existing one:

```csharp
    /// <summary>
    /// Used only by MartenCompanyRepositoryFactory for cross-module atomic writes — the session is
    /// already bound to a shared transaction the caller owns. Intentionally never disposed by this
    /// class in that path; see Global Constraints in
    /// docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
    /// </summary>
    public MartenCompanyRepository(IDocumentSession session)
    {
        _session = session;
    }
```

- [ ] **Step 4: Add the factory implementation**

`src/Companies/Companies.Infrastructure/Common/MartenCompanyRepositoryFactory.cs`:

```csharp
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Companies.Infrastructure.Common;

public sealed class MartenCompanyRepositoryFactory(ICompaniesStore store, ICurrentGameServer currentGameServer) : ICompanyRepositoryFactory
{
    // Tracking mode is left at SessionOptions' default deliberately — this session is only ever used
    // for `Events.Append`, never for loading-then-`Store`-ing a mutated document, so dirty-tracking
    // vs. lightweight tracking makes no behavioral difference here.
    public ICompanyRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var options = SessionOptions.ForTransaction(handle.Unwrap(), shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenCompanyRepository(session);
    }
}
```

- [ ] **Step 5: Register the factory in DI**

In `src/Companies/Companies.Infrastructure/ServiceCollectionExtensions.cs`, add after the existing `services.TryAddScoped<ICompanyRepository, MartenCompanyRepository>();` line:

```csharp
        services.TryAddScoped<ICompanyRepositoryFactory, MartenCompanyRepositoryFactory>();
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/Companies/Companies.Infrastructure/Companies.Infrastructure.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Companies/Companies.Application src/Companies/Companies.Infrastructure
git commit -m "feat: add ICompanyRepositoryFactory for cross-module atomic writes"
```

---

### Task 5: Banking cross-module write support

**Files:**
- Modify: `src/Banking/Banking.Application/Banking.Application.csproj`
- Modify: `src/Banking/Banking.Infrastructure/Banking.Infrastructure.csproj`
- Create: `src/Banking/Banking.Application/Common/IBankAccountRepositoryFactory.cs`
- Modify: `src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs`
- Create: `src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepositoryFactory.cs`
- Modify: `src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `CrossModuleSessionHandle`, `CrossModuleSessionHandleExtensions.Unwrap` (Tasks 1, 3).
- Produces: `IBankAccountRepositoryFactory { IBankAccountRepository CreateFor(CrossModuleSessionHandle handle); }`, `MartenBankAccountRepository(IDocumentSession session)` (new constructor overload).

Mirror of Task 4, for Banking.

- [ ] **Step 1: Add the project references**

In `src/Banking/Banking.Application/Banking.Application.csproj`, add to the existing `<ItemGroup>` with `ProjectReference`s:

```xml
    <!-- For IBankAccountRepositoryFactory's CrossModuleSessionHandle parameter — see ARCHITECTURE.md §9e. -->
    <ProjectReference Include="..\..\Shared\Shared.Integration.Abstractions\Shared.Integration.Abstractions.csproj" />
```

In `src/Banking/Banking.Infrastructure/Banking.Infrastructure.csproj`, add to the existing `<ItemGroup>` with `ProjectReference`s:

```xml
    <ProjectReference Include="..\..\Shared\Shared.Integration\Shared.Integration.csproj" />
```

- [ ] **Step 2: Add the factory interface**

`src/Banking/Banking.Application/Common/IBankAccountRepositoryFactory.cs`:

```csharp
using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Builds a Banking repository bound to a shared cross-module transaction's session instead of this
/// module's normal per-request session — used only by orchestrating commands (e.g.
/// PurchaseCompanySharesCommand) that write to Banking and another module atomically. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface IBankAccountRepositoryFactory
{
    IBankAccountRepository CreateFor(CrossModuleSessionHandle handle);
}
```

- [ ] **Step 3: Add the external-session constructor to `MartenBankAccountRepository`**

In `src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepository.cs`, add a second constructor right after the existing one:

```csharp
    /// <summary>
    /// Used only by MartenBankAccountRepositoryFactory for cross-module atomic writes — the session
    /// is already bound to a shared transaction the caller owns. Intentionally never disposed by
    /// this class in that path; see Global Constraints in
    /// docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
    /// </summary>
    public MartenBankAccountRepository(IDocumentSession session)
    {
        _session = session;
    }
```

- [ ] **Step 4: Add the factory implementation**

`src/Banking/Banking.Infrastructure/Common/MartenBankAccountRepositoryFactory.cs`:

```csharp
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Marten.Services;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed class MartenBankAccountRepositoryFactory(IBankingStore store, ICurrentGameServer currentGameServer) : IBankAccountRepositoryFactory
{
    // Tracking mode is left at SessionOptions' default deliberately — this session is only ever used
    // for `Events.Append`, never for loading-then-`Store`-ing a mutated document, so dirty-tracking
    // vs. lightweight tracking makes no behavioral difference here.
    public IBankAccountRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var options = SessionOptions.ForTransaction(handle.Unwrap(), shouldAutoCommit: false);
        options.TenantId = currentGameServer.ClientId;

        var session = store.OpenSession(options);
        return new MartenBankAccountRepository(session);
    }
}
```

- [ ] **Step 5: Register the factory in DI**

In `src/Banking/Banking.Infrastructure/ServiceCollectionExtensions.cs`, add after the existing `services.TryAddScoped<IBankAccountRepository, MartenBankAccountRepository>();` line:

```csharp
        services.TryAddScoped<IBankAccountRepositoryFactory, MartenBankAccountRepositoryFactory>();
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/Banking/Banking.Infrastructure/Banking.Infrastructure.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Banking/Banking.Application src/Banking/Banking.Infrastructure
git commit -m "feat: add IBankAccountRepositoryFactory for cross-module atomic writes"
```

---

### Task 6: Wire the composition root

**Files:**
- Modify: `src/Api/appsettings.Development.json`
- Modify: `src/Api/Program.cs`
- Modify: `tests/Banking.IntegrationTests/TestServices.cs`
- Modify: `.claude/worktrees/shops-feature/src/Api/appsettings.Development.json` (if that worktree still exists — see Step 4)

**Interfaces:**
- Consumes: `IServiceCollection.AddCrossModuleIntegration(IConfiguration)` (Task 3).

- [ ] **Step 1: Add the shared connection string**

In `src/Api/appsettings.Development.json`, add a new key to `ConnectionStrings` (after `CompanyDatabase`):

```json
    "SharedDatabase": "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
```

- [ ] **Step 2: Register `AddCrossModuleIntegration` in the host**

In `src/Api/Program.cs`, add this line right after `builder.Services.AddCompanyModule(builder.Configuration);`:

```csharp
builder.Services.AddCrossModuleIntegration(builder.Configuration);
```

- [ ] **Step 3: Update the integration test fixture**

In `tests/Banking.IntegrationTests/TestServices.cs`, add a new key to the in-memory configuration dictionary (after `["ConnectionStrings:CompanyDatabase"]`):

```csharp
                ["ConnectionStrings:SharedDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
```

Add this line after `services.AddCompanyInfrastructure(configuration);`:

```csharp
        services.AddCrossModuleIntegration(configuration);
```

- [ ] **Step 4: Check for the `shops-feature` worktree**

Run: `test -f .claude/worktrees/shops-feature/src/Api/appsettings.Development.json && echo exists || echo gone`

If `exists`, that worktree has its own copy of `appsettings.Development.json` on a different branch — leave it alone (it's a separate in-progress feature branch, not part of this plan's scope; it will pick up this change when/if it rebases). If `gone`, nothing to do.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Api/appsettings.Development.json src/Api/Program.cs tests/Banking.IntegrationTests/TestServices.cs
git commit -m "feat: wire up cross-module integration in the composition root"
```

---

### Task 7: `PurchaseCompanySharesCommand` and handler

**Files:**
- Create: `src/Banking/Banking.Application/Companies/PurchaseCompanySharesCommand.cs`
- Test: `tests/Banking.IntegrationTests/PurchaseCompanySharesCommandTests.cs`

**Interfaces:**
- Consumes: `IBankAccountRepositoryFactory`, `ICompanyRepositoryFactory` (Tasks 4, 5), `ICrossModuleTransactionFactory` (Task 1/3), `BankAccountAuthorization.IsAuthorizedAsync` (existing, `Banking.Application.Common`), `Company.IssueShares` (Task 2).
- Produces: `PurchaseCompanySharesCommand(BankAccountId PayerBankAccountId, CharacterId Buyer, CompanyId CompanyId, int Quantity, decimal PricePerShare) : IRequest<PurchaseCompanySharesResult>`, `PurchaseCompanySharesResult` union with cases `Purchased(int Quantity, decimal TotalPaid, decimal NewBalance)`, `BankAccountNotFound`, `CompanyNotFound`, `NotAuthorized`, `InsufficientBalance`, `InvalidQuantity`.

This is the task that actually proves the mechanism works — its integration tests are the real deliverable, not a formality.

- [ ] **Step 1: Write the failing tests**

`tests/Banking.IntegrationTests/PurchaseCompanySharesCommandTests.cs`:

```csharp
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Application.Companies;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Proves PurchaseCompanySharesCommand's cross-module atomicity: each
/// non-happy-path test reloads state from Postgres afterward to confirm nothing was partially
/// persisted, not just that an error was returned. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public sealed class PurchaseCompanySharesCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
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

    [Fact]
    public async Task Purchase_WithSufficientBalance_DebitsBuyerAndIssuesShares()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(bankAccountId, 1000m));
        var companyId = await CreateCompanyAsync(mediator, buyerId);

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyId, 10, 5m));

        Assert.True(result is PurchaseCompanySharesResult.Purchased, $"Expected Purchased, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(accountDetails is BankAccountDetailsResult.Found, $"Expected Found, got {accountDetails}");
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.True(found.BankAccount.Balance < 950m, "Balance should be reduced by 50 (10 * 5) plus fee.");
        }

        var companyDetails = await mediator.Send(new CompanyDetailsQuery(companyId));
        Assert.True(companyDetails is CompanyDetailsResult.Found, $"Expected Found, got {companyDetails}");
        if (companyDetails is CompanyDetailsResult.Found companyFound)
        {
            Assert.Contains(companyFound.Company.Shares, s => s.CharacterId == buyerId && s.Quantity == 10);
        }
    }

    [Fact]
    public async Task Purchase_WithInsufficientBalance_LeavesBankAccountAndCompanyUnchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(bankAccountId, 10m));
        var companyId = await CreateCompanyAsync(mediator, buyerId);

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyId, 1000, 5m));

        Assert.True(result is PurchaseCompanySharesResult.InsufficientBalance, $"Expected InsufficientBalance, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.Equal(10m, found.BankAccount.Balance);
        }

        var companyDetails = await mediator.Send(new CompanyDetailsQuery(companyId));
        if (companyDetails is CompanyDetailsResult.Found companyFound)
        {
            Assert.Empty(companyFound.Company.Shares);
        }
    }

    [Fact]
    public async Task Purchase_ForUnknownCompany_LeavesBankAccountUnchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(bankAccountId, 1000m));

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, new CompanyId(Guid.NewGuid()), 10, 5m));

        Assert.True(result is PurchaseCompanySharesResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.Equal(1000m, found.BankAccount.Balance);
        }
    }

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync(mediator);
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Shares Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private static async Task<BankId> OpenBankAsync(IMediator mediator)
    {
        var result = await mediator.Send(new OpenBankCommand("Test Bank", 0.20m, 0.02m));
        return result.Id;
    }

    private async Task<BankAccountId> OpenBankAccountAsync(IMediator mediator, CharacterId characterId)
    {
        var bankId = await OpenBankAsync(mediator);
        var result = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }

    private static async Task<CompanyId> CreateCompanyAsync(IMediator mediator, CharacterId founderCharacterId)
    {
        var result = await mediator.Send(new CreateCompanyCommand("Test Company", founderCharacterId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CompanyId;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Precondition: `docker compose up -d` running, devcontainer connected to its network.

Run: `dotnet test tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj --filter "FullyQualifiedName~PurchaseCompanySharesCommandTests"`
Expected: FAIL to build — `PurchaseCompanySharesCommand`, `PurchaseCompanySharesResult` don't exist yet.

- [ ] **Step 3: Implement the command and handler**

`src/Banking/Banking.Application/Companies/PurchaseCompanySharesCommand.cs`:

```csharp
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Banking.Application.Companies;

public union PurchaseCompanySharesResult(
    PurchaseCompanySharesResult.Purchased,
    PurchaseCompanySharesResult.BankAccountNotFound,
    PurchaseCompanySharesResult.CompanyNotFound,
    PurchaseCompanySharesResult.NotAuthorized,
    PurchaseCompanySharesResult.InsufficientBalance,
    PurchaseCompanySharesResult.InvalidQuantity)
{
    public record Purchased(int Quantity, decimal TotalPaid, decimal NewBalance);

    public record BankAccountNotFound;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;

    public record InvalidQuantity;
}

public sealed record PurchaseCompanySharesCommand(
    BankAccountId PayerBankAccountId,
    CharacterId Buyer,
    CompanyId CompanyId,
    int Quantity,
    decimal PricePerShare) : IRequest<PurchaseCompanySharesResult>;

public sealed class PurchaseCompanySharesHandler(
    ICrossModuleTransactionFactory transactionFactory,
    IBankAccountRepositoryFactory bankAccountRepositoryFactory,
    ICompanyRepositoryFactory companyRepositoryFactory,
    IMediator mediator)
    : IRequestHandler<PurchaseCompanySharesCommand, PurchaseCompanySharesResult>
{
    public async ValueTask<PurchaseCompanySharesResult> Handle(PurchaseCompanySharesCommand request, CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0)
        {
            return new PurchaseCompanySharesResult.InvalidQuantity();
        }

        await using var transaction = await transactionFactory.BeginAsync(cancellationToken);

        // Repositories obtained from a cross-module transaction handle are intentionally never
        // disposed here — only `transaction` owns the underlying connection/transaction. See Global
        // Constraints in docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
        var bankAccountRepository = bankAccountRepositoryFactory.CreateFor(transaction.Handle);
        var bankAccount = await bankAccountRepository.FindByIdAsync(request.PayerBankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new PurchaseCompanySharesResult.BankAccountNotFound();
        }

        var companyRepository = companyRepositoryFactory.CreateFor(transaction.Handle);
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new PurchaseCompanySharesResult.CompanyNotFound();
        }

        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(bankAccount, request.Buyer, mediator, cancellationToken);
        var totalPrice = request.Quantity * request.PricePerShare;

        BankAccountWithdrawn withdrawnEvent;
        try
        {
            withdrawnEvent = bankAccount.Withdraw(request.Buyer, isAuthorized, totalPrice);
        }
        catch (BankAccountAuthorizationException)
        {
            return new PurchaseCompanySharesResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new PurchaseCompanySharesResult.InsufficientBalance();
        }

        var issuedEvent = company.IssueShares(request.Buyer, request.Quantity);

        bankAccountRepository.Append(request.PayerBankAccountId, withdrawnEvent);
        companyRepository.Append(request.CompanyId, issuedEvent);

        await bankAccountRepository.SaveChangesAsync(cancellationToken);
        await companyRepository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PurchaseCompanySharesResult.Purchased(request.Quantity, totalPrice, bankAccount.Balance);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj --filter "FullyQualifiedName~PurchaseCompanySharesCommandTests"`
Expected: PASS — 3 tests passed.

- [ ] **Step 5: Run the full Banking.IntegrationTests suite to confirm no regression**

Run: `dotnet test tests/Banking.IntegrationTests/Banking.IntegrationTests.csproj`
Expected: PASS — all tests (existing + new) pass.

- [ ] **Step 6: Commit**

```bash
git add src/Banking/Banking.Application/Companies tests/Banking.IntegrationTests/PurchaseCompanySharesCommandTests.cs
git commit -m "feat: add PurchaseCompanySharesCommand — first cross-module atomic write"
```

---

### Task 8: API endpoint and docs

**Files:**
- Create: `src/Banking/Banking.Api/BankAccounts/PurchaseCompanySharesRequestDto.cs`
- Modify: `src/Banking/Banking.Api/BankingEndpoints.cs`
- Modify: `docs/banking.md`

**Interfaces:**
- Consumes: `PurchaseCompanySharesCommand`, `PurchaseCompanySharesResult` (Task 7).

- [ ] **Step 1: Add the request DTO**

`src/Banking/Banking.Api/BankAccounts/PurchaseCompanySharesRequestDto.cs`:

```csharp
namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record PurchaseCompanySharesRequestDto
{
    public required Guid CharacterId { get; init; }

    public required Guid CompanyId { get; init; }

    public required int Quantity { get; init; }

    public required decimal PricePerShare { get; init; }

    public PurchaseCompanySharesCommand ToCommand(Guid bankAccountId) =>
        new(new BankAccountId(bankAccountId), new CharacterId(CharacterId), new CompanyId(CompanyId), Quantity, PricePerShare);
}
```

- [ ] **Step 2: Add the endpoint**

In `src/Banking/Banking.Api/BankingEndpoints.cs`, add after the existing `group.MapPut("bank-accounts/{bankAccountId:guid}/transaction", ...)` block (before `return app;`):

```csharp
        group.MapPut("bank-accounts/{bankAccountId:guid}/purchase-company-shares", async (
                Guid bankAccountId,
                [FromBody] PurchaseCompanySharesRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(bankAccountId), cancellationToken);

                return result switch
                {
                    PurchaseCompanySharesResult.Purchased purchased => Results.Ok(new TransactionResultDto
                    {
                        Amount = purchased.TotalPaid,
                        Fee = 0m,
                        NewBalance = purchased.NewBalance,
                    }),
                    PurchaseCompanySharesResult.BankAccountNotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseCompanySharesResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseCompanySharesResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to transact on this account",
                        statusCode: StatusCodes.Status403Forbidden),
                    PurchaseCompanySharesResult.InsufficientBalance => Results.Problem(
                        title: "Insufficient balance",
                        statusCode: StatusCodes.Status409Conflict),
                    PurchaseCompanySharesResult.InvalidQuantity => Results.Problem(
                        title: "Quantity must be positive",
                        statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<TransactionResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PurchaseCompanyShares")
            .WithDescription("Debits a bank account and issues company shares to the acting character, atomically across the Banking and Companies modules.");

```

Add `using ELifeRPG.Banking.Application.Companies;` to the top of `BankingEndpoints.cs`, alongside the existing `using` block.

- [ ] **Step 3: Update the docs walkthrough**

In `docs/banking.md`, add a new section after "Transaction history" (before the final "(These assume you're running..." line):

```markdown
## Company shares

`PUT /api/bank-accounts/{id}/purchase-company-shares` debits the account for `quantity * pricePerShare` and credits the acting character with shares in the given company, atomically — if either side would fail, neither is applied:

```sh
curl -X PUT http://localhost:5100/api/bank-accounts/$BANK_ACCOUNT_ID/purchase-company-shares \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"characterId":"<characterId>","companyId":"<companyId>","quantity":10,"pricePerShare":5.00}'
```
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build ELifeRPG.Core.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Banking/Banking.Api docs/banking.md
git commit -m "feat: expose PurchaseCompanyShares over HTTP"
```

---

### Task 9: Update `ARCHITECTURE.md`

**Files:**
- Modify: `ARCHITECTURE.md`

- [ ] **Step 1: Document the new cross-module write exception**

In the "Dependency rules" bullet list (§9e), add a new bullet immediately after the existing bullet that starts "**No module project may reference another module's `Domain`, `Infrastructure`, or `Api` project...**" (the one ending "...enforced by convention/review now, not by the compiler."):

```markdown
- **Cross-module atomic writes** are a second, narrower exception to the isolation rule above: an orchestrating command in one module's `Application` layer may call another module's explicitly-named `I<X>RepositoryFactory.CreateFor(handle)` (e.g. `ICompanyRepositoryFactory`) to obtain a repository bound to a shared `ICrossModuleTransaction`, when a single operation must commit events to both modules atomically — this is possible because every module's data lives in the same physical PostgreSQL database, just separate schemas. It never reaches into `Domain`/`Infrastructure`/`Api` directly, only a factory the target module chooses to expose from its own `Application` layer, and is reserved for named, individually-reviewed orchestrating commands (e.g. `Banking.Application.Companies.PurchaseCompanySharesCommand`), not a general write-anywhere mechanism. See `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`.
```

- [ ] **Step 2: Fix the dangling saga/process-manager reference**

Replace the final sentence of the "Multi-aggregate atomic operations" paragraph (§9e):

Old:
```
This only works because both aggregates are internal to the *same* module and the *same* repository instance — a transfer spanning two modules would need the saga/process-manager approach `ARCHITECTURE.md §8` already reserves for that case, not this shortcut.
```

New:
```
This only works because both aggregates are internal to the *same* module and the *same* repository instance — a transfer spanning two modules instead uses the cross-module atomic-write mechanism described above (`ICrossModuleTransaction`), not this shortcut. Confirmed via `Banking`'s `PurchaseCompanySharesCommand`, which atomically debits a `BankAccount` and credits `Companies`' `Company.Shares` in one shared Postgres transaction — see `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`.
```

- [ ] **Step 3: Review the diff**

Run: `git diff ARCHITECTURE.md`
Expected: Exactly the two changes above, no unrelated edits.

- [ ] **Step 4: Commit**

```bash
git add ARCHITECTURE.md
git commit -m "docs: document cross-module atomic write mechanism in ARCHITECTURE.md"
```
