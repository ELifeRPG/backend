# Player Whitelist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let staff gate a server's `player-connected` token issuance behind a reviewed, free-text whitelist application submitted by an existing `Account`, with per-server opt-in and a generalized `GameServer` settings resource for future server-level config.

**Architecture:** New `WhitelistApplication` event-sourced aggregate (own Marten stream, in the `Accounts` module) with an `Open → InReview → Approved/Rejected` state machine; a new plain-document `GameServer` resource keyed by the caller's own Keycloak `client_id` claim; `session-bootstrap` gains a `ServerClientId` parameter and a third `SessionStatus` value (`NotWhitelisted`) alongside the existing `Active`/`Blocked`; the actual token-withholding gate is a one-line generalization of the already-landed `BridgeTokenProvider.ExchangeForPlayerTokenAsync` status check, in the separate `eliferpg-reforger-bridge` repo. A new Keycloak realm role (`whitelist-reviewer`) — the first role-based (not scope-based) authorization policy in this codebase — gates review/admin actions.

**Tech Stack:** .NET 11 preview, Marten (Postgres), `Mediator` (source-generated, not MediatR), native `union` declarations, `StronglyTypedId`, ASP.NET Minimal APIs, Keycloak (realm roles + client scopes).

**Spec:** `docs/superpowers/specs/2026-08-15-player-whitelist-design.md`

## Global Constraints

- Every endpoint returns data, not errors, for expected business outcomes where the spec says so (`session-bootstrap` always 200; `NotWhitelisted` is a `Status` value like `"blocked"`, never an HTTP error).
- No new `.csproj`/module boundary — everything lives inside the existing `Accounts.Domain`/`Accounts.Application`/`Accounts.Infrastructure`/`Accounts.Api` projects, per the spec's "fold into Accounts" decision.
- `ApplicationText` capped at 4000 characters, validated at the endpoint (400 Bad Request over the cap), mirroring `CompanyEndpoints.cs`'s existing `Message.Length > 1000` check for `SubmitApplicationRequestDto`.
- `Approve`/`Reject` are only valid from `InReview`; `StartReview` and each terminal decision are idempotent on their own already-reached state (return success, not an error) — see spec's "Domain model" section.
- Bridge-side changes land in the separate `eliferpg-reforger-bridge` repo (`/home/kevin/dev/projects/eliferpg-reforger-bridge`), not this one — Bridge was split out of `eliferpg-core` in commit `eea4b0c`, after this spec's first draft.
- Empirically confirmed against the live dev Keycloak (not assumed): the caller's own client id is on the flat `client_id` claim (e.g. `"client_id": "gameserver-dev"`); realm roles are on the `realm_access` claim as a JSON object `{"roles": [...]}`, present only when the granting client actually has a role mapping for it — `staff-admin-dev` currently has `fullScopeAllowed: false` and no realm role mappings at all, so granting `whitelist-reviewer` requires an explicit role mapping on its service-account user, not reliance on default/full-scope behavior.

---

## Task 1: `WhitelistApplication` domain aggregate

**Files:**
- Create: `src/Accounts/Accounts.Domain/WhitelistApplicationId.cs`
- Create: `src/Accounts/Accounts.Domain/WhitelistApplicationStatus.cs`
- Create: `src/Accounts/Accounts.Domain/Events/WhitelistApplicationSubmitted.cs`
- Create: `src/Accounts/Accounts.Domain/Events/WhitelistApplicationReviewStarted.cs`
- Create: `src/Accounts/Accounts.Domain/Events/WhitelistApplicationApproved.cs`
- Create: `src/Accounts/Accounts.Domain/Events/WhitelistApplicationRejected.cs`
- Create: `src/Accounts/Accounts.Domain/Exceptions/WhitelistApplicationStatusException.cs`
- Create: `src/Accounts/Accounts.Domain/WhitelistApplication.cs`
- Test: `tests/Accounts.Domain.UnitTests/WhitelistApplicationTests.cs`

**Interfaces:**
- Consumes: `AccountId` (`ELifeRPG.Shared.Kernel`, existing).
- Produces: `WhitelistApplicationId`, `WhitelistApplicationStatus { Open, InReview, Approved, Rejected }`, `WhitelistApplication` with `Id`, `AccountId`, `ServerClientId` (string), `ApplicationText` (string), `Status`; `WhitelistApplication.Create(WhitelistApplicationSubmitted)`, `.StartReview()`, `.Approve()`, `.Reject()` — each returns its event (or `null` if it's an idempotent no-op on an already-reached state) and applies it. `WhitelistApplicationStatusException` for genuinely invalid transitions.

- [ ] **Step 1: Write the failing domain unit test**

```csharp
// tests/Accounts.Domain.UnitTests/WhitelistApplicationTests.cs
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public sealed class WhitelistApplicationTests
{
    private static WhitelistApplication CreateOpen() => WhitelistApplication.Create(new WhitelistApplicationSubmitted(
        new WhitelistApplicationId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "gameserver-dev", "let me in please"));

    [Fact]
    public void Create_SetsFieldsFromEvent_AndStatusOpen()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var id = new WhitelistApplicationId(Guid.NewGuid());
        var @event = new WhitelistApplicationSubmitted(id, accountId, "gameserver-dev", "text");

        var application = WhitelistApplication.Create(@event);

        Assert.Equal(id, application.Id);
        Assert.Equal(accountId, application.AccountId);
        Assert.Equal("gameserver-dev", application.ServerClientId);
        Assert.Equal("text", application.ApplicationText);
        Assert.Equal(WhitelistApplicationStatus.Open, application.Status);
    }

    [Fact]
    public void StartReview_FromOpen_TransitionsToInReview()
    {
        var application = CreateOpen();

        var @event = application.StartReview();

        Assert.NotNull(@event);
        Assert.Equal(WhitelistApplicationStatus.InReview, application.Status);
    }

    [Fact]
    public void StartReview_AlreadyInReview_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();

        var @event = application.StartReview();

        Assert.Null(@event);
        Assert.Equal(WhitelistApplicationStatus.InReview, application.Status);
    }

    [Fact]
    public void StartReview_AlreadyApproved_Throws()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Approve();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.StartReview());
    }

    [Fact]
    public void Approve_FromInReview_TransitionsToApproved()
    {
        var application = CreateOpen();
        application.StartReview();

        var @event = application.Approve();

        Assert.NotNull(@event);
        Assert.Equal(WhitelistApplicationStatus.Approved, application.Status);
    }

    [Fact]
    public void Approve_AlreadyApproved_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Approve();

        var @event = application.Approve();

        Assert.Null(@event);
        Assert.Equal(WhitelistApplicationStatus.Approved, application.Status);
    }

    [Fact]
    public void Approve_FromOpen_Throws()
    {
        var application = CreateOpen();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.Approve());
    }

    [Fact]
    public void Approve_AlreadyRejected_Throws()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Reject();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.Approve());
    }

    [Fact]
    public void Reject_FromInReview_TransitionsToRejected()
    {
        var application = CreateOpen();
        application.StartReview();

        var @event = application.Reject();

        Assert.NotNull(@event);
        Assert.Equal(WhitelistApplicationStatus.Rejected, application.Status);
    }

    [Fact]
    public void Reject_AlreadyRejected_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Reject();

        var @event = application.Reject();

        Assert.Null(@event);
        Assert.Equal(WhitelistApplicationStatus.Rejected, application.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.Domain.UnitTests --filter WhitelistApplicationTests"`
Expected: FAIL to compile — `WhitelistApplication`/`WhitelistApplicationId`/etc. don't exist yet.

- [ ] **Step 3: Create `WhitelistApplicationId.cs`**

```csharp
namespace ELifeRPG.Accounts.Domain;

[StronglyTypedId]
public partial struct WhitelistApplicationId;
```

- [ ] **Step 4: Create `WhitelistApplicationStatus.cs`**

```csharp
namespace ELifeRPG.Accounts.Domain;

public enum WhitelistApplicationStatus
{
    Open,
    InReview,
    Approved,
    Rejected,
}
```

- [ ] **Step 5: Create the four event records**

```csharp
// src/Accounts/Accounts.Domain/Events/WhitelistApplicationSubmitted.cs
namespace ELifeRPG.Accounts.Domain.Events;

public sealed record WhitelistApplicationSubmitted(WhitelistApplicationId Id, AccountId AccountId, string ServerClientId, string ApplicationText);
```

```csharp
// src/Accounts/Accounts.Domain/Events/WhitelistApplicationReviewStarted.cs
namespace ELifeRPG.Accounts.Domain.Events;

public sealed record WhitelistApplicationReviewStarted(WhitelistApplicationId Id);
```

```csharp
// src/Accounts/Accounts.Domain/Events/WhitelistApplicationApproved.cs
namespace ELifeRPG.Accounts.Domain.Events;

public sealed record WhitelistApplicationApproved(WhitelistApplicationId Id);
```

```csharp
// src/Accounts/Accounts.Domain/Events/WhitelistApplicationRejected.cs
namespace ELifeRPG.Accounts.Domain.Events;

public sealed record WhitelistApplicationRejected(WhitelistApplicationId Id);
```

- [ ] **Step 6: Create `WhitelistApplicationStatusException.cs`**

```csharp
namespace ELifeRPG.Accounts.Domain.Exceptions;

public sealed class WhitelistApplicationStatusException(string message) : InvalidOperationException(message);
```

- [ ] **Step 7: Implement `WhitelistApplication.cs`**

```csharp
using System.Text.Json.Serialization;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

public class WhitelistApplication
{
    [JsonInclude]
    public WhitelistApplicationId Id { get; private set; }

    [JsonInclude]
    public AccountId AccountId { get; private set; }

    [JsonInclude]
    public string ServerClientId { get; private set; } = string.Empty;

    [JsonInclude]
    public string ApplicationText { get; private set; } = string.Empty;

    [JsonInclude]
    public WhitelistApplicationStatus Status { get; private set; } = WhitelistApplicationStatus.Open;

    public static WhitelistApplication Create(WhitelistApplicationSubmitted @event)
    {
        var application = new WhitelistApplication();
        application.Apply(@event);
        return application;
    }

    public WhitelistApplicationReviewStarted? StartReview()
    {
        if (Status == WhitelistApplicationStatus.InReview)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.Open)
        {
            throw new WhitelistApplicationStatusException("Application must be Open to start review.");
        }

        var @event = new WhitelistApplicationReviewStarted(Id);
        Apply(@event);
        return @event;
    }

    public WhitelistApplicationApproved? Approve()
    {
        if (Status == WhitelistApplicationStatus.Approved)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.InReview)
        {
            throw new WhitelistApplicationStatusException("Application must be InReview to be approved.");
        }

        var @event = new WhitelistApplicationApproved(Id);
        Apply(@event);
        return @event;
    }

    public WhitelistApplicationRejected? Reject()
    {
        if (Status == WhitelistApplicationStatus.Rejected)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.InReview)
        {
            throw new WhitelistApplicationStatusException("Application must be InReview to be rejected.");
        }

        var @event = new WhitelistApplicationRejected(Id);
        Apply(@event);
        return @event;
    }

    public void Apply(WhitelistApplicationSubmitted @event)
    {
        Id = @event.Id;
        AccountId = @event.AccountId;
        ServerClientId = @event.ServerClientId;
        ApplicationText = @event.ApplicationText;
    }

    public void Apply(WhitelistApplicationReviewStarted @event) => Status = WhitelistApplicationStatus.InReview;

    public void Apply(WhitelistApplicationApproved @event) => Status = WhitelistApplicationStatus.Approved;

    public void Apply(WhitelistApplicationRejected @event) => Status = WhitelistApplicationStatus.Rejected;
}
```

- [ ] **Step 8: Run test to verify it passes**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.Domain.UnitTests --filter WhitelistApplicationTests"`
Expected: PASS, all 9 facts.

- [ ] **Step 9: Commit**

```bash
git add src/Accounts/Accounts.Domain tests/Accounts.Domain.UnitTests/WhitelistApplicationTests.cs
git commit -m "feat(accounts): add WhitelistApplication domain aggregate"
```

---

## Task 2: `GameServer` settings document

**Files:**
- Create: `src/Accounts/Accounts.Domain/GameServer.cs`

**Interfaces:**
- Produces: `GameServer { ClientId: string, WhitelistEnabled: bool }` — a plain Marten document (no event stream), per spec.

- [ ] **Step 1: Create `GameServer.cs`**

```csharp
namespace ELifeRPG.Accounts.Domain;

public sealed class GameServer
{
    public required string ClientId { get; init; }

    public bool WhitelistEnabled { get; set; }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Accounts/Accounts.Domain/GameServer.cs
git commit -m "feat(accounts): add GameServer settings document type"
```

No standalone test here — behavior is exercised through `IGameServerRepository`'s integration tests in Task 4.

---

## Task 3: Infrastructure — Marten projection and repositories

**Files:**
- Create: `src/Accounts/Accounts.Infrastructure/Common/WhitelistApplicationProjection.cs`
- Create: `src/Accounts/Accounts.Application/Common/IWhitelistApplicationRepository.cs`
- Create: `src/Accounts/Accounts.Infrastructure/Common/MartenWhitelistApplicationRepository.cs`
- Create: `src/Accounts/Accounts.Application/Common/IGameServerRepository.cs`
- Create: `src/Accounts/Accounts.Infrastructure/Common/MartenGameServerRepository.cs`
- Modify: `src/Accounts/Accounts.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/Accounts.IntegrationTests/WhitelistApplicationRepositoryTests.cs`
- Test: `tests/Accounts.IntegrationTests/GameServerRepositoryTests.cs`

**Interfaces:**
- Consumes: `WhitelistApplication`, `WhitelistApplicationId`, `WhitelistApplicationSubmitted`, `GameServer` (Task 1/2).
- Produces:
  ```csharp
  public interface IWhitelistApplicationRepository
  {
      ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken);
      ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);
      ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);
      ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken);
      void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted @event);
      void Append<TEvent>(WhitelistApplicationId id, TEvent @event) where TEvent : notnull;
      ValueTask SaveChangesAsync(CancellationToken cancellationToken);
  }

  public interface IGameServerRepository
  {
      ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken cancellationToken); // never null
      ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/Accounts.IntegrationTests/WhitelistApplicationRepositoryTests.cs
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>Requires the local infra stack (`docker compose up -d`) — see README.md.</summary>
public sealed class WhitelistApplicationRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task FindPendingAsync_AfterSubmit_ReturnsTheOpenApplication()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var @event = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "gameserver-dev", "text");
        var application = WhitelistApplication.Create(@event);
        repository.StartStream(application, @event);
        await repository.SaveChangesAsync(CancellationToken.None);

        var pending = await repository.FindPendingAsync(accountId, "gameserver-dev", CancellationToken.None);

        Assert.NotNull(pending);
        Assert.Equal(application.Id, pending!.Id);
    }

    [Fact]
    public async Task FindApprovedAsync_BeforeApproval_ReturnsNull()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var @event = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "gameserver-dev", "text");
        var application = WhitelistApplication.Create(@event);
        repository.StartStream(application, @event);
        await repository.SaveChangesAsync(CancellationToken.None);

        var approved = await repository.FindApprovedAsync(accountId, "gameserver-dev", CancellationToken.None);

        Assert.Null(approved);
    }

    [Fact]
    public async Task FindApprovedAsync_AfterApproval_ReturnsIt()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var submitted = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "gameserver-dev", "text");
        var application = WhitelistApplication.Create(submitted);
        repository.StartStream(application, submitted);
        await repository.SaveChangesAsync(CancellationToken.None);

        var reviewStarted = application.StartReview()!;
        repository.Append(application.Id, reviewStarted);
        var approved = application.Approve()!;
        repository.Append(application.Id, approved);
        await repository.SaveChangesAsync(CancellationToken.None);

        var found = await repository.FindApprovedAsync(accountId, "gameserver-dev", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(WhitelistApplicationStatus.Approved, found!.Status);
    }
}
```

```csharp
// tests/Accounts.IntegrationTests/GameServerRepositoryTests.cs
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>Requires the local infra stack (`docker compose up -d`) — see README.md.</summary>
public sealed class GameServerRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task GetOrDefaultAsync_NoRecord_ReturnsWhitelistDisabled()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var clientId = $"never-configured-{Guid.NewGuid()}";

        var server = await repository.GetOrDefaultAsync(clientId, CancellationToken.None);

        Assert.Equal(clientId, server.ClientId);
        Assert.False(server.WhitelistEnabled);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetOrDefaultAsync_RoundTrips()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var clientId = $"toggle-test-{Guid.NewGuid()}";

        await repository.UpsertAsync(new GameServer { ClientId = clientId, WhitelistEnabled = true }, CancellationToken.None);
        var server = await repository.GetOrDefaultAsync(clientId, CancellationToken.None);

        Assert.True(server.WhitelistEnabled);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter WhitelistApplicationRepositoryTests|GameServerRepositoryTests"`
Expected: FAIL to compile — `IWhitelistApplicationRepository`/`IGameServerRepository` don't exist yet.

- [ ] **Step 3: Create the two repository interfaces**

```csharp
// src/Accounts/Accounts.Application/Common/IWhitelistApplicationRepository.cs
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IWhitelistApplicationRepository
{
    ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken);

    void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted @event);

    void Append<TEvent>(WhitelistApplicationId id, TEvent @event) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
```

```csharp
// src/Accounts/Accounts.Application/Common/IGameServerRepository.cs
namespace ELifeRPG.Accounts.Application.Common;

public interface IGameServerRepository
{
    ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken cancellationToken);

    ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create the Marten projection**

```csharp
// src/Accounts/Accounts.Infrastructure/Common/WhitelistApplicationProjection.cs
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class WhitelistApplicationProjection : SingleStreamProjection<WhitelistApplication, WhitelistApplicationId>
{
    public static WhitelistApplication Create(WhitelistApplicationSubmitted @event) => WhitelistApplication.Create(@event);

    public void Apply(WhitelistApplication application, WhitelistApplicationReviewStarted @event) => application.Apply(@event);

    public void Apply(WhitelistApplication application, WhitelistApplicationApproved @event) => application.Apply(@event);

    public void Apply(WhitelistApplication application, WhitelistApplicationRejected @event) => application.Apply(@event);
}
```

- [ ] **Step 5: Implement `MartenWhitelistApplicationRepository.cs`**

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenWhitelistApplicationRepository(IDocumentSession session) : IWhitelistApplicationRepository
{
    public async ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken)
        => await session.LoadAsync<WhitelistApplication>(id, cancellationToken);

    public async ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>()
            .Where(x => x.AccountId == accountId && x.ServerClientId == serverClientId
                && (x.Status == WhitelistApplicationStatus.Open || x.Status == WhitelistApplicationStatus.InReview))
            .FirstOrDefaultAsync(cancellationToken);

    public async ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>()
            .Where(x => x.AccountId == accountId && x.ServerClientId == serverClientId && x.Status == WhitelistApplicationStatus.Approved)
            .FirstOrDefaultAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>().Where(x => x.Status == status).ToListAsync(cancellationToken);

    public void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted @event)
        => session.Events.StartStream<WhitelistApplication>(application.Id.Value, @event);

    public void Append<TEvent>(WhitelistApplicationId id, TEvent @event) where TEvent : notnull
        => session.Events.Append(id.Value, @event);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => new(session.SaveChangesAsync(cancellationToken));
}
```

- [ ] **Step 6: Implement `MartenGameServerRepository.cs`**

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenGameServerRepository(IDocumentSession session) : IGameServerRepository
{
    public async ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken cancellationToken)
        => await session.LoadAsync<GameServer>(clientId, cancellationToken)
            ?? new GameServer { ClientId = clientId, WhitelistEnabled = false };

    public async ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken)
    {
        session.Store(server);
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 7: Wire both into `ServiceCollectionExtensions.cs`**

Modify `src/Accounts/Accounts.Infrastructure/ServiceCollectionExtensions.cs`. `GameServer`'s document id is its `ClientId` (a string) — Marten needs an explicit identity mapping since the property isn't named `Id`. Add inside the existing `options.AddMarten(options => { ... })` block, after the existing `options.Projections.Add<AccountProjection>(...)` line:

```csharp
options.Projections.Add<WhitelistApplicationProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
options.Schema.For<GameServer>().Identity(x => x.ClientId);
```

And after the existing `services.TryAddScoped<IAccountRepository, MartenAccountRepository>();` line:

```csharp
services.TryAddScoped<IWhitelistApplicationRepository, MartenWhitelistApplicationRepository>();
services.TryAddScoped<IGameServerRepository, MartenGameServerRepository>();
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter WhitelistApplicationRepositoryTests|GameServerRepositoryTests"`
Expected: PASS, all 5 facts. If Marten complains about an unmapped document, re-check the `Identity(x => x.ClientId)` line landed inside the same `AddMarten` options block as the rest of the account schema config.

- [ ] **Step 9: Commit**

```bash
git add src/Accounts/Accounts.Infrastructure src/Accounts/Accounts.Application/Common tests/Accounts.IntegrationTests/WhitelistApplicationRepositoryTests.cs tests/Accounts.IntegrationTests/GameServerRepositoryTests.cs
git commit -m "feat(accounts): add Marten projection and repositories for whitelist applications and game servers"
```

---

## Task 4: Application commands — submit, review, approve, reject, list, update settings

**Files:**
- Create: `src/Accounts/Accounts.Application/Whitelist/SubmitWhitelistApplicationCommand.cs`
- Create: `src/Accounts/Accounts.Application/Whitelist/StartWhitelistApplicationReviewCommand.cs`
- Create: `src/Accounts/Accounts.Application/Whitelist/ApproveWhitelistApplicationCommand.cs`
- Create: `src/Accounts/Accounts.Application/Whitelist/RejectWhitelistApplicationCommand.cs`
- Create: `src/Accounts/Accounts.Application/Whitelist/WhitelistApplicationsQuery.cs`
- Create: `src/Accounts/Accounts.Application/GameServers/UpdateGameServerSettingsCommand.cs`
- Create: `src/Accounts/Accounts.Application/GameServers/GameServerLookupQuery.cs`
- Test: `tests/Accounts.IntegrationTests/SubmitWhitelistApplicationCommandTests.cs`
- Test: `tests/Accounts.IntegrationTests/ReviewWhitelistApplicationCommandTests.cs`

**Interfaces:**
- Consumes: `IWhitelistApplicationRepository`, `IGameServerRepository` (Task 3), `IAccountRepository` (existing).
- Produces:
  ```csharp
  public union SubmitWhitelistApplicationResult(Submitted, AccountNotFound, AlreadyPending)
  public sealed record SubmitWhitelistApplicationCommand(AccountId AccountId, string ServerClientId, string ApplicationText) : IRequest<SubmitWhitelistApplicationResult>;

  public union StartWhitelistApplicationReviewResult(Started, NotFound)
  public sealed record StartWhitelistApplicationReviewCommand(WhitelistApplicationId Id) : IRequest<StartWhitelistApplicationReviewResult>;

  public union ApproveWhitelistApplicationResult(Approved, NotFound, InvalidState)
  public sealed record ApproveWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<ApproveWhitelistApplicationResult>;

  public union RejectWhitelistApplicationResult(Rejected, NotFound, InvalidState)
  public sealed record RejectWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<RejectWhitelistApplicationResult>;

  public union WhitelistApplicationsResult(Found(IReadOnlyList<WhitelistApplication> Applications))
  public sealed record WhitelistApplicationsQuery(WhitelistApplicationStatus Status) : IRequest<WhitelistApplicationsResult>;

  public sealed record UpdateGameServerSettingsCommand(string ClientId, bool? WhitelistEnabled) : IRequest<GameServer>;

  public sealed record GameServerLookupQuery(string ClientId) : IRequest<GameServer>;
  ```

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/Accounts.IntegrationTests/SubmitWhitelistApplicationCommandTests.cs
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Application.Whitelist;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>Requires the local infra stack (`docker compose up -d`) — see README.md.</summary>
public sealed class SubmitWhitelistApplicationCommandTests : IAsyncLifetime
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

    private async Task<AccountId> CreateAccountAsync()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bohemiaId = new GameId(Guid.NewGuid());
        var session = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));
        _createdUsernames.Add(session.KeycloakUsername);
        return session.AccountId;
    }

    [Fact]
    public async Task Handle_ExistingAccount_ReturnsSubmitted()
    {
        var accountId = await CreateAccountAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "gameserver-dev", "let me in"));

        Assert.IsType<SubmitWhitelistApplicationResult.Submitted>(result);
    }

    [Fact]
    public async Task Handle_UnknownAccount_ReturnsAccountNotFound()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(new AccountId(Guid.NewGuid()), "gameserver-dev", "text"));

        Assert.IsType<SubmitWhitelistApplicationResult.AccountNotFound>(result);
    }

    [Fact]
    public async Task Handle_AlreadyPendingForSameServer_ReturnsAlreadyPending()
    {
        var accountId = await CreateAccountAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "gameserver-dev", "first"));

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "gameserver-dev", "second"));

        Assert.IsType<SubmitWhitelistApplicationResult.AlreadyPending>(result);
    }
}
```

```csharp
// tests/Accounts.IntegrationTests/ReviewWhitelistApplicationCommandTests.cs
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Application.Whitelist;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>Requires the local infra stack (`docker compose up -d`) — see README.md.</summary>
public sealed class ReviewWhitelistApplicationCommandTests : IAsyncLifetime
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

    private async Task<WhitelistApplicationId> SubmitApplicationAsync()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bohemiaId = new GameId(Guid.NewGuid());
        var session = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));
        _createdUsernames.Add(session.KeycloakUsername);
        var submitted = (SubmitWhitelistApplicationResult.Submitted)await mediator.Send(
            new SubmitWhitelistApplicationCommand(session.AccountId, "gameserver-dev", "text"));
        return submitted.WhitelistApplicationId;
    }

    [Fact]
    public async Task ApproveWithoutStartingReview_ReturnsInvalidState()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.IsType<ApproveWhitelistApplicationResult.InvalidState>(result);
    }

    [Fact]
    public async Task StartReviewThenApprove_ReturnsApproved()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new StartWhitelistApplicationReviewCommand(id));

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.IsType<ApproveWhitelistApplicationResult.Approved>(result);
    }

    [Fact]
    public async Task ApproveTwice_SecondCallIsIdempotent()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new StartWhitelistApplicationReviewCommand(id));
        await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.IsType<ApproveWhitelistApplicationResult.Approved>(result);
    }

    [Fact]
    public async Task ApproveUnknownId_ReturnsNotFound()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(new WhitelistApplicationId(Guid.NewGuid())));

        Assert.IsType<ApproveWhitelistApplicationResult.NotFound>(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter SubmitWhitelistApplicationCommandTests|ReviewWhitelistApplicationCommandTests"`
Expected: FAIL to compile — commands don't exist yet. (Also update `CreateSessionCommand(bohemiaId)` call sites — this task's tests already assume the Task 6 two-argument signature; if Task 6 hasn't landed yet when running this task alone, temporarily call `new CreateSessionCommand(bohemiaId, "gameserver-dev")` will fail to compile until Task 6 lands. Do Task 6 first if working out of order, or accept this task's tests stay red until Task 6 is done — this plan is written in dependency order, so by the time you reach Task 4's Step 8 you should already be on Task 6 per the task list... reorder if executing task-by-task in strict numeric order by pulling Task 6 forward, OR temporarily stub with `new CreateSessionCommand(bohemiaId, "gameserver-dev")` and revisit — see note at Task 6.)

- [ ] **Step 3: Implement `SubmitWhitelistApplicationCommand.cs`**

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union SubmitWhitelistApplicationResult(
    SubmitWhitelistApplicationResult.Submitted,
    SubmitWhitelistApplicationResult.AccountNotFound,
    SubmitWhitelistApplicationResult.AlreadyPending)
{
    public record Submitted(WhitelistApplicationId WhitelistApplicationId);

    public record AccountNotFound;

    public record AlreadyPending;
}

public sealed record SubmitWhitelistApplicationCommand(AccountId AccountId, string ServerClientId, string ApplicationText)
    : IRequest<SubmitWhitelistApplicationResult>;

public sealed class SubmitWhitelistApplicationHandler(IAccountRepository accountRepository, IWhitelistApplicationRepository whitelistRepository)
    : IRequestHandler<SubmitWhitelistApplicationCommand, SubmitWhitelistApplicationResult>
{
    public async ValueTask<SubmitWhitelistApplicationResult> Handle(SubmitWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new SubmitWhitelistApplicationResult.AccountNotFound();
        }

        var pending = await whitelistRepository.FindPendingAsync(request.AccountId, request.ServerClientId, cancellationToken);
        if (pending is not null)
        {
            return new SubmitWhitelistApplicationResult.AlreadyPending();
        }

        var id = new WhitelistApplicationId(Guid.NewGuid());
        var @event = new WhitelistApplicationSubmitted(id, request.AccountId, request.ServerClientId, request.ApplicationText);
        var application = WhitelistApplication.Create(@event);

        whitelistRepository.StartStream(application, @event);
        await whitelistRepository.SaveChangesAsync(cancellationToken);

        return new SubmitWhitelistApplicationResult.Submitted(id);
    }
}
```

- [ ] **Step 4: Implement `StartWhitelistApplicationReviewCommand.cs`, `ApproveWhitelistApplicationCommand.cs`, `RejectWhitelistApplicationCommand.cs`**

```csharp
// src/Accounts/Accounts.Application/Whitelist/StartWhitelistApplicationReviewCommand.cs
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union StartWhitelistApplicationReviewResult(StartWhitelistApplicationReviewResult.Started, StartWhitelistApplicationReviewResult.NotFound)
{
    public record Started;

    public record NotFound;
}

public sealed record StartWhitelistApplicationReviewCommand(WhitelistApplicationId Id) : IRequest<StartWhitelistApplicationReviewResult>;

public sealed class StartWhitelistApplicationReviewHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<StartWhitelistApplicationReviewCommand, StartWhitelistApplicationReviewResult>
{
    public async ValueTask<StartWhitelistApplicationReviewResult> Handle(StartWhitelistApplicationReviewCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new StartWhitelistApplicationReviewResult.NotFound();
        }

        var @event = application.StartReview();
        if (@event is not null)
        {
            repository.Append(request.Id, @event);
            await repository.SaveChangesAsync(cancellationToken);
        }

        return new StartWhitelistApplicationReviewResult.Started();
    }
}
```

```csharp
// src/Accounts/Accounts.Application/Whitelist/ApproveWhitelistApplicationCommand.cs
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union ApproveWhitelistApplicationResult(
    ApproveWhitelistApplicationResult.Approved,
    ApproveWhitelistApplicationResult.NotFound,
    ApproveWhitelistApplicationResult.InvalidState)
{
    public record Approved;

    public record NotFound;

    public record InvalidState;
}

public sealed record ApproveWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<ApproveWhitelistApplicationResult>;

public sealed class ApproveWhitelistApplicationHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<ApproveWhitelistApplicationCommand, ApproveWhitelistApplicationResult>
{
    public async ValueTask<ApproveWhitelistApplicationResult> Handle(ApproveWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new ApproveWhitelistApplicationResult.NotFound();
        }

        try
        {
            var @event = application.Approve();
            if (@event is not null)
            {
                repository.Append(request.Id, @event);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (WhitelistApplicationStatusException)
        {
            return new ApproveWhitelistApplicationResult.InvalidState();
        }

        return new ApproveWhitelistApplicationResult.Approved();
    }
}
```

```csharp
// src/Accounts/Accounts.Application/Whitelist/RejectWhitelistApplicationCommand.cs
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union RejectWhitelistApplicationResult(
    RejectWhitelistApplicationResult.Rejected,
    RejectWhitelistApplicationResult.NotFound,
    RejectWhitelistApplicationResult.InvalidState)
{
    public record Rejected;

    public record NotFound;

    public record InvalidState;
}

public sealed record RejectWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<RejectWhitelistApplicationResult>;

public sealed class RejectWhitelistApplicationHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<RejectWhitelistApplicationCommand, RejectWhitelistApplicationResult>
{
    public async ValueTask<RejectWhitelistApplicationResult> Handle(RejectWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new RejectWhitelistApplicationResult.NotFound();
        }

        try
        {
            var @event = application.Reject();
            if (@event is not null)
            {
                repository.Append(request.Id, @event);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (WhitelistApplicationStatusException)
        {
            return new RejectWhitelistApplicationResult.InvalidState();
        }

        return new RejectWhitelistApplicationResult.Rejected();
    }
}
```

- [ ] **Step 5: Implement `WhitelistApplicationsQuery.cs`**

```csharp
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union WhitelistApplicationsResult(WhitelistApplicationsResult.Found)
{
    public record Found(IReadOnlyList<WhitelistApplication> Applications);
}

public sealed record WhitelistApplicationsQuery(WhitelistApplicationStatus Status) : IRequest<WhitelistApplicationsResult>;

public sealed class WhitelistApplicationsHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<WhitelistApplicationsQuery, WhitelistApplicationsResult>
{
    public async ValueTask<WhitelistApplicationsResult> Handle(WhitelistApplicationsQuery request, CancellationToken cancellationToken)
        => new WhitelistApplicationsResult.Found(await repository.ListByStatusAsync(request.Status, cancellationToken));
}
```

- [ ] **Step 6: Implement `UpdateGameServerSettingsCommand.cs` and `GameServerLookupQuery.cs`**

```csharp
// src/Accounts/Accounts.Application/GameServers/UpdateGameServerSettingsCommand.cs
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record UpdateGameServerSettingsCommand(string ClientId, bool? WhitelistEnabled) : IRequest<GameServer>;

public sealed class UpdateGameServerSettingsHandler(IGameServerRepository repository)
    : IRequestHandler<UpdateGameServerSettingsCommand, GameServer>
{
    public async ValueTask<GameServer> Handle(UpdateGameServerSettingsCommand request, CancellationToken cancellationToken)
    {
        var server = await repository.GetOrDefaultAsync(request.ClientId, cancellationToken);
        if (request.WhitelistEnabled is { } whitelistEnabled)
        {
            server.WhitelistEnabled = whitelistEnabled;
        }

        await repository.UpsertAsync(server, cancellationToken);
        return server;
    }
}
```

```csharp
// src/Accounts/Accounts.Application/GameServers/GameServerLookupQuery.cs
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.GameServers;

public sealed record GameServerLookupQuery(string ClientId) : IRequest<GameServer>;

public sealed class GameServerLookupHandler(IGameServerRepository repository) : IRequestHandler<GameServerLookupQuery, GameServer>
{
    public async ValueTask<GameServer> Handle(GameServerLookupQuery request, CancellationToken cancellationToken)
        => await repository.GetOrDefaultAsync(request.ClientId, cancellationToken);
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter SubmitWhitelistApplicationCommandTests|ReviewWhitelistApplicationCommandTests"`
Expected: PASS (requires Task 6's `CreateSessionCommand` signature change to already be in place — do Task 6 before running this step if not already done).

- [ ] **Step 8: Commit**

```bash
git add src/Accounts/Accounts.Application/Whitelist src/Accounts/Accounts.Application/GameServers tests/Accounts.IntegrationTests/SubmitWhitelistApplicationCommandTests.cs tests/Accounts.IntegrationTests/ReviewWhitelistApplicationCommandTests.cs
git commit -m "feat(accounts): add whitelist application and game server settings commands"
```

---

## Task 5: Keycloak realm — `whitelist-reviewer` role and `gameserver:whitelist:write` scope

**Files:**
- Modify (live Keycloak, then re-export): `infra/keycloak/eliferpg-realm.json`

This task changes the **running** dev Keycloak directly via its Admin REST API first (so the rest of this plan's manual verification and integration tests actually work against real auth), then re-exports and patches secrets back in — exactly the workflow README.md already documents for persisting realm changes.

- [ ] **Step 1: Get an admin token and create the `whitelist-reviewer` realm role**

```bash
ADMIN_TOKEN=$(curl -s -X POST http://localhost:8180/realms/master/protocol/openid-connect/token \
  -d "client_id=admin-cli" -d "username=admin" -d "password=admin" -d "grant_type=password" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -s -X POST http://localhost:8180/admin/realms/eliferpg/roles \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "whitelist-reviewer", "description": "Can review, approve, reject whitelist applications and manage per-server whitelist settings."}'
```

Expected: `204` (or `201`), no body.

- [ ] **Step 2: Add the `gameserver:whitelist:write` client scope**

```bash
curl -s -X POST http://localhost:8180/admin/realms/eliferpg/client-scopes \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "gameserver:whitelist:write", "protocol": "openid-connect", "attributes": {"include.in.token.scope": "true", "display.on.consent.screen": "false"}}'
```

Expected: `201`.

- [ ] **Step 3: Grant that scope to `gameserver-dev` as a default client scope**

```bash
GAMESERVER_CLIENT_UUID=$(curl -s "http://localhost:8180/admin/realms/eliferpg/clients?clientId=gameserver-dev" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | python3 -c "import json,sys; print(json.load(sys.stdin)[0]['id'])")
WHITELIST_SCOPE_ID=$(curl -s "http://localhost:8180/admin/realms/eliferpg/client-scopes" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | python3 -c "import json,sys; print(next(s['id'] for s in json.load(sys.stdin) if s['name']=='gameserver:whitelist:write'))")

curl -s -X PUT "http://localhost:8180/admin/realms/eliferpg/clients/$GAMESERVER_CLIENT_UUID/default-client-scopes/$WHITELIST_SCOPE_ID" \
  -H "Authorization: Bearer $ADMIN_TOKEN"
```

Expected: `204`.

- [ ] **Step 4: Grant `whitelist-reviewer` to `staff-admin-dev`'s service account user**

`staff-admin-dev` has `fullScopeAllowed: false` and (confirmed empirically) no realm role mappings at all today, so this must be an explicit role-mapping call, not something that falls out of defaults:

```bash
STAFF_CLIENT_UUID=$(curl -s "http://localhost:8180/admin/realms/eliferpg/clients?clientId=staff-admin-dev" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | python3 -c "import json,sys; print(json.load(sys.stdin)[0]['id'])")
STAFF_SERVICE_ACCOUNT_ID=$(curl -s "http://localhost:8180/admin/realms/eliferpg/clients/$STAFF_CLIENT_UUID/service-account-user" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])")
WHITELIST_ROLE=$(curl -s "http://localhost:8180/admin/realms/eliferpg/roles/whitelist-reviewer" \
  -H "Authorization: Bearer $ADMIN_TOKEN")

curl -s -X POST "http://localhost:8180/admin/realms/eliferpg/users/$STAFF_SERVICE_ACCOUNT_ID/role-mappings/realm" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d "[$WHITELIST_ROLE]"
```

Expected: `204`. **This alone is not enough** — confirmed empirically: with `fullScopeAllowed: false`, a role assigned directly to the user still doesn't appear in that client's tokens unless the *client* also has a scope-mapping for it (a separate, client-level restriction on which of the user's roles it's allowed to surface). Add that too:

```bash
curl -s -X POST "http://localhost:8180/admin/realms/eliferpg/clients/$STAFF_CLIENT_UUID/scope-mappings/realm" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d "[$WHITELIST_ROLE]"
```

Expected: `204`. (`gameserver-dev` never needed this second call because it has `fullScopeAllowed: true`.)

- [ ] **Step 5: Verify by minting a fresh `staff-admin-dev` token and decoding it**

```bash
python3 - <<'PYEOF'
import urllib.request, urllib.parse, json, base64

data = urllib.parse.urlencode({"client_id": "staff-admin-dev", "client_secret": "staff-secret-change-me", "grant_type": "client_credentials"}).encode()
req = urllib.request.Request("http://localhost:8180/realms/eliferpg/protocol/openid-connect/token", data=data)
with urllib.request.urlopen(req) as resp:
    token = json.load(resp)["access_token"]

payload = token.split(".")[1]
payload += "=" * (-len(payload) % 4)
claims = json.loads(base64.urlsafe_b64decode(payload))
print(json.dumps(claims.get("realm_access"), indent=2))
PYEOF
```

Expected: `{"roles": ["whitelist-reviewer"]}` (or that plus other roles — the point is `whitelist-reviewer` appears).

- [ ] **Step 6: Re-export the realm and patch secrets back in**

```bash
curl -s "http://localhost:8180/admin/realms/eliferpg/partial-export?exportClients=true&exportGroupsAndRoles=true" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -X POST -H "Content-Type: application/json" -d '{}' \
  > infra/keycloak/eliferpg-realm.json
```

Then manually diff against the previous version and restore the `secret` field for `gameserver-dev`/`account-service`/`staff-admin-dev` (Keycloak redacts secrets on export — same caveat the README already documents), and confirm the export includes: the `whitelist-reviewer` realm role, the `gameserver:whitelist:write` client scope, `gameserver-dev`'s `defaultClientScopes` including it, and a `service-account-staff-admin-dev` user entry with `realmRoles: ["whitelist-reviewer"]`.

- [ ] **Step 7: Commit**

```bash
git add infra/keycloak/eliferpg-realm.json
git commit -m "feat(keycloak): add whitelist-reviewer realm role and gameserver:whitelist:write scope"
```

---

## Task 6: `session-bootstrap` gate — `ServerClientId` and `SessionStatus.NotWhitelisted`

**Files:**
- Modify: `src/Accounts/Accounts.Application/Sessions/CreateSessionCommand.cs`
- Modify: `src/Accounts/Accounts.Api/Sessions/AccountEndpoints.cs`
- Modify: `src/Accounts/Accounts.Api/Sessions/CreateSessionRequestDto.cs`
- Modify: `src/Accounts/Accounts.Api/Sessions/SessionDto.cs`
- Modify: `tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs`
- Test: `tests/Accounts.IntegrationTests/CreateSessionCommandWhitelistGateTests.cs`

**Note:** Tasks 4's tests already assume this task's two-argument `CreateSessionCommand(GameId, string)` signature — do this task before running Task 4's Step 7, or right after Task 4's Step 6, whichever you reach first; the two are interdependent by design (both touch account/session plumbing).

**Interfaces:**
- Consumes: `IGameServerRepository`, `IWhitelistApplicationRepository` (Task 3).
- Produces: `CreateSessionCommand(GameId BohemiaId, string ServerClientId)`; `SessionStatus { Active, Blocked, NotWhitelisted }`; `CreateSessionResponse(AccountId AccountId, string KeycloakUsername, SessionStatus Status)`.

- [ ] **Step 1: Write the failing gate test**

```csharp
// tests/Accounts.IntegrationTests/CreateSessionCommandWhitelistGateTests.cs
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Application.Whitelist;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>Requires the local infra stack (`docker compose up -d`) — see README.md.</summary>
public sealed class CreateSessionCommandWhitelistGateTests : IAsyncLifetime
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
    public async Task Handle_WhitelistOffForServer_ReturnsActiveRegardlessOfApplications()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverClientId = $"whitelist-off-{Guid.NewGuid()}";
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(SessionStatus.Active, result.Status);
    }

    [Fact]
    public async Task Handle_WhitelistOnNoApplication_ReturnsNotWhitelisted()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var gameServerRepository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var serverClientId = $"whitelist-on-{Guid.NewGuid()}";
        await gameServerRepository.UpsertAsync(new GameServer { ClientId = serverClientId, WhitelistEnabled = true }, CancellationToken.None);
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(SessionStatus.NotWhitelisted, result.Status);
    }

    [Fact]
    public async Task Handle_WhitelistOnWithApprovedApplication_ReturnsActive()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var gameServerRepository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var serverClientId = $"whitelist-approved-{Guid.NewGuid()}";
        await gameServerRepository.UpsertAsync(new GameServer { ClientId = serverClientId, WhitelistEnabled = true }, CancellationToken.None);
        var bohemiaId = new GameId(Guid.NewGuid());

        var first = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));
        _createdUsernames.Add(first.KeycloakUsername);
        var submitted = (SubmitWhitelistApplicationResult.Submitted)await mediator.Send(
            new SubmitWhitelistApplicationCommand(first.AccountId, serverClientId, "text"));
        await mediator.Send(new StartWhitelistApplicationReviewCommand(submitted.WhitelistApplicationId));
        await mediator.Send(new ApproveWhitelistApplicationCommand(submitted.WhitelistApplicationId));

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        Assert.Equal(SessionStatus.Active, result.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter CreateSessionCommandWhitelistGateTests"`
Expected: FAIL to compile — `CreateSessionCommand` still takes one argument, `SessionStatus` doesn't exist.

- [ ] **Step 3: Modify `CreateSessionCommand.cs`**

Replace the whole file:

```csharp
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Sessions;

public enum SessionStatus
{
    Active,
    Blocked,
    NotWhitelisted,
}

public sealed record CreateSessionResponse(AccountId AccountId, string KeycloakUsername, SessionStatus Status);

public sealed record CreateSessionCommand(GameId BohemiaId, string ServerClientId) : IRequest<CreateSessionResponse>;

public sealed class CreateSessionHandler(
    IAccountRepository accountRepository,
    IKeycloakUserProvisioner keycloakUserProvisioner,
    IGameServerRepository gameServerRepository,
    IWhitelistApplicationRepository whitelistRepository)
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

        var status = await ResolveStatusAsync(account, request.ServerClientId, cancellationToken);

        return new CreateSessionResponse(account.Id, KeycloakUsername.For(account.BohemiaId), status);
    }

    private async ValueTask<SessionStatus> ResolveStatusAsync(Account account, string serverClientId, CancellationToken cancellationToken)
    {
        if (account.Status == AccountStatus.Locked)
        {
            return SessionStatus.Blocked;
        }

        var server = await gameServerRepository.GetOrDefaultAsync(serverClientId, cancellationToken);
        if (!server.WhitelistEnabled)
        {
            return SessionStatus.Active;
        }

        var approved = await whitelistRepository.FindApprovedAsync(account.Id, serverClientId, cancellationToken);
        return approved is null ? SessionStatus.NotWhitelisted : SessionStatus.Active;
    }
}
```

- [ ] **Step 4: Modify `CreateSessionRequestDto.cs`**

The DTO can no longer build the command on its own — `ServerClientId` comes from the caller's own token claim, resolved in the endpoint, not the request body. Replace `ToCommand()` with a method that takes it:

```csharp
namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record CreateSessionRequestDto
{
    public required Guid BohemiaId { get; init; }

    public CreateSessionCommand ToCommand(string serverClientId) => new(new GameId(BohemiaId), serverClientId);
}
```

- [ ] **Step 5: Modify `SessionDto.cs`**

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
        Status = source.Status switch
        {
            SessionStatus.Blocked => "blocked",
            SessionStatus.NotWhitelisted => "not_whitelisted",
            _ => "active",
        },
    };
}
```

- [ ] **Step 6: Modify the `session-bootstrap` endpoint mapping in `AccountEndpoints.cs`**

Change the `session-bootstrap` handler to read the caller's own `client_id` claim (confirmed present on every Client Credentials token — Task 5's spike) and pass it through:

```csharp
group.MapPost("session-bootstrap", async (
        [FromBody] CreateSessionRequestDto request,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken) =>
    {
        var serverClientId = user.FindFirst("client_id")?.Value ?? string.Empty;
        var result = await mediator.Send(request.ToCommand(serverClientId), cancellationToken);
        return Results.Ok(SessionDto.Create(result));
    })
    .RequireAuthorization(SessionCreatePolicy)
    .Produces<SessionDto>()
    .WithName("BootstrapSession")
    .WithDescription("Bootstraps (or looks up) a session for a player's Bohemia ID, provisioning an account if needed. Always returns 200 — a blocked or not-whitelisted account is reported via the Status field, not an error.");
```

Add `using System.Security.Claims;` to the top of the file if not already present.

- [ ] **Step 7: Update the existing `CreateSessionCommandTests.cs` call sites**

In `tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs`, change both `new CreateSessionCommand(bohemiaId)` calls to `new CreateSessionCommand(bohemiaId, "gameserver-dev")`, and change `Assert.Equal(AccountStatus.Active, result.Status)` to `Assert.Equal(SessionStatus.Active, result.Status)` (add `using ELifeRPG.Accounts.Application.Sessions;` if not already present for `SessionStatus`).

- [ ] **Step 8: Run all affected tests**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet test tests/Accounts.IntegrationTests --filter CreateSessionCommandTests|CreateSessionCommandWhitelistGateTests|LockAccountCommandTests|SubmitWhitelistApplicationCommandTests|ReviewWhitelistApplicationCommandTests"`
Expected: PASS across the board — this step also re-validates Task 4's tests, which depend on this task's signature change.

- [ ] **Step 9: Commit**

```bash
git add src/Accounts/Accounts.Application/Sessions src/Accounts/Accounts.Api/Sessions tests/Accounts.IntegrationTests/CreateSessionCommandTests.cs tests/Accounts.IntegrationTests/CreateSessionCommandWhitelistGateTests.cs
git commit -m "feat(accounts): gate session-bootstrap status on per-server whitelist approval"
```

---

## Task 7: HTTP endpoints — whitelist applications and game server settings

**Files:**
- Create: `src/Accounts/Accounts.Api/Whitelist/WhitelistEndpoints.cs`
- Create: `src/Accounts/Accounts.Api/Whitelist/SubmitWhitelistApplicationRequestDto.cs`
- Create: `src/Accounts/Accounts.Api/Whitelist/WhitelistApplicationDto.cs`
- Create: `src/Accounts/Accounts.Api/GameServers/GameServerEndpoints.cs`
- Create: `src/Accounts/Accounts.Api/GameServers/GameServerDto.cs`
- Create: `src/Accounts/Accounts.Api/GameServers/UpdateGameServerSettingsRequestDto.cs`
- Modify: `src/Api/Program.cs`

**Interfaces:**
- Consumes: all commands/queries from Task 4, `ClaimsPrincipal` claim reading pattern from Task 6.
- Produces: `AddWhitelistModule`/`MapWhitelistModule`, `AddGameServerModule`/`MapGameServerModule`, both registered in `Program.cs`.

- [ ] **Step 1: Implement the whitelist DTOs**

```csharp
// src/Accounts/Accounts.Api/Whitelist/SubmitWhitelistApplicationRequestDto.cs
namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record SubmitWhitelistApplicationRequestDto
{
    public required Guid AccountId { get; init; }

    public required string ApplicationText { get; init; }

    public SubmitWhitelistApplicationCommand ToCommand(string serverClientId) =>
        new(new AccountId(AccountId), serverClientId, ApplicationText);
}
```

```csharp
// src/Accounts/Accounts.Api/Whitelist/WhitelistApplicationDto.cs
namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record WhitelistApplicationDto
{
    public required Guid WhitelistApplicationId { get; init; }

    public required Guid AccountId { get; init; }

    public required string ServerClientId { get; init; }

    public required string ApplicationText { get; init; }

    public required string Status { get; init; }

    public static WhitelistApplicationDto Create(WhitelistApplication source) => new()
    {
        WhitelistApplicationId = source.Id.Value,
        AccountId = source.AccountId.Value,
        ServerClientId = source.ServerClientId,
        ApplicationText = source.ApplicationText,
        Status = source.Status.ToString(),
    };
}
```

- [ ] **Step 2: Implement `WhitelistEndpoints.cs`**

```csharp
using ELifeRPG.Accounts.Api.Whitelist;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class WhitelistModule
{
    public const string WhitelistWriteScope = "gameserver:whitelist:write";
    public const string WhitelistReviewerRole = "whitelist-reviewer";
    private const string WhitelistWritePolicy = "Whitelist.Write";
    private const string WhitelistReviewerPolicy = "Whitelist.Reviewer";

    public static IServiceCollection AddWhitelistModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(WhitelistWritePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(WhitelistWriteScope)))
            .AddPolicy(WhitelistReviewerPolicy, policy => policy.RequireAssertion(context => HasReviewerRole(context.User)));

        return services;
    }

    private static bool HasReviewerRole(ClaimsPrincipal user)
    {
        var realmAccessJson = user.FindFirst("realm_access")?.Value;
        if (realmAccessJson is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(realmAccessJson);
        return document.RootElement.TryGetProperty("roles", out var roles)
            && roles.EnumerateArray().Any(role => role.GetString() == WhitelistReviewerRole);
    }

    public static WebApplication MapWhitelistModule(this WebApplication app)
    {
        var group = app.MapGroup("api/whitelist-applications").WithTags("Whitelist");

        group.MapPost("", async (
                [FromBody] SubmitWhitelistApplicationRequestDto request,
                ClaimsPrincipal user,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request.ApplicationText.Length > 4000)
                {
                    return Results.Problem(
                        title: "Application text must be 4000 characters or fewer",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var serverClientId = user.FindFirst("client_id")?.Value ?? string.Empty;
                var result = await mediator.Send(request.ToCommand(serverClientId), cancellationToken);

                return result switch
                {
                    SubmitWhitelistApplicationResult.Submitted submitted => Results.Ok(new
                    {
                        whitelistApplicationId = submitted.WhitelistApplicationId.Value,
                        status = "Open",
                    }),
                    SubmitWhitelistApplicationResult.AccountNotFound => Results.Problem(
                        title: "Account not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitWhitelistApplicationResult.AlreadyPending => Results.Problem(
                        title: "Account already has a pending application for this server",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistWritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SubmitWhitelistApplication")
            .WithDescription("Submits an account's whitelist application for the calling server.");

        group.MapPost("{id:guid}/start-review", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new StartWhitelistApplicationReviewCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    StartWhitelistApplicationReviewResult.Started => Results.NoContent(),
                    StartWhitelistApplicationReviewResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("StartWhitelistApplicationReview")
            .WithDescription("Marks an Open application as InReview. Idempotent if already InReview.");

        group.MapPost("{id:guid}/approve", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ApproveWhitelistApplicationCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    ApproveWhitelistApplicationResult.Approved => Results.NoContent(),
                    ApproveWhitelistApplicationResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    ApproveWhitelistApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be InReview to be approved", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApproveWhitelistApplication")
            .WithDescription("Approves an InReview application. Idempotent if already Approved.");

        group.MapPost("{id:guid}/reject", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RejectWhitelistApplicationCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    RejectWhitelistApplicationResult.Rejected => Results.NoContent(),
                    RejectWhitelistApplicationResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    RejectWhitelistApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be InReview to be rejected", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RejectWhitelistApplication")
            .WithDescription("Rejects an InReview application. Idempotent if already Rejected.");

        group.MapGet("", async (
                [FromQuery] WhitelistApplicationStatus status,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new WhitelistApplicationsQuery(status), cancellationToken);
                return Results.Ok(((WhitelistApplicationsResult.Found)result).Applications.Select(WhitelistApplicationDto.Create).ToList());
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .Produces<List<WhitelistApplicationDto>>()
            .WithName("ListWhitelistApplications")
            .WithDescription("Lists whitelist applications by status, for the review queue.");

        return app;
    }
}
```

- [ ] **Step 3: Implement the game server DTOs and endpoints**

```csharp
// src/Accounts/Accounts.Api/GameServers/GameServerDto.cs
namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record GameServerDto
{
    public required string ClientId { get; init; }

    public required bool WhitelistEnabled { get; init; }

    public static GameServerDto Create(GameServer source) => new()
    {
        ClientId = source.ClientId,
        WhitelistEnabled = source.WhitelistEnabled,
    };
}
```

```csharp
// src/Accounts/Accounts.Api/GameServers/UpdateGameServerSettingsRequestDto.cs
namespace ELifeRPG.Accounts.Api.GameServers;

public sealed record UpdateGameServerSettingsRequestDto
{
    public bool? WhitelistEnabled { get; init; }

    public UpdateGameServerSettingsCommand ToCommand(string clientId) => new(clientId, WhitelistEnabled);
}
```

```csharp
// src/Accounts/Accounts.Api/GameServers/GameServerEndpoints.cs
using ELifeRPG.Accounts.Api.GameServers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class GameServerModule
{
    public static WebApplication MapGameServerModule(this WebApplication app)
    {
        var group = app.MapGroup("api/game-servers").WithTags("GameServers");

        group.MapGet("{clientId}", async (
                string clientId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var server = await mediator.Send(new GameServerLookupQuery(clientId), cancellationToken);
                return Results.Ok(GameServerDto.Create(server));
            })
            .RequireAuthorization(WhitelistModule.WhitelistReviewerPolicy())
            .Produces<GameServerDto>()
            .WithName("GetGameServer")
            .WithDescription("Gets a server's settings, defaulted if never configured.");

        group.MapPatch("{clientId}", async (
                string clientId,
                [FromBody] UpdateGameServerSettingsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var server = await mediator.Send(request.ToCommand(clientId), cancellationToken);
                return Results.Ok(GameServerDto.Create(server));
            })
            .RequireAuthorization(WhitelistModule.WhitelistReviewerPolicy())
            .Produces<GameServerDto>()
            .WithName("UpdateGameServerSettings")
            .WithDescription("Partially updates a server's settings (e.g. WhitelistEnabled). Omitted fields are left unchanged.");

        return app;
    }
}
```

`WhitelistModule.WhitelistReviewerPolicy()` doesn't exist as a public accessor yet — the policy name constant in `WhitelistEndpoints.cs` is `private const string WhitelistReviewerPolicy`. Change it to `public const string WhitelistReviewerPolicy = "Whitelist.Reviewer";` (and likewise `WhitelistWritePolicy` if anything outside the file ever needs it — for now only `WhitelistReviewerPolicy` is consumed cross-file, so only that one needs to become public) so `GameServerEndpoints.cs` can reference `WhitelistModule.WhitelistReviewerPolicy` directly as a field, not a method call — fix the two `.RequireAuthorization(WhitelistModule.WhitelistReviewerPolicy())` calls above to `.RequireAuthorization(WhitelistModule.WhitelistReviewerPolicy)` (field, no parens) once that visibility change is made.

- [ ] **Step 4: Register both modules in `Program.cs`**

Add after the existing `builder.Services.AddAccountModule(builder.Configuration);` line:

```csharp
builder.Services.AddWhitelistModule(builder.Configuration);
```

(`GameServerModule` needs no `AddGameServerModule` — it declares no new policies of its own, only maps endpoints, so nothing to register besides mapping.)

Add after the existing `app.MapAccountModule();` line:

```csharp
app.MapWhitelistModule();
app.MapGameServerModule();
```

- [ ] **Step 5: Build**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet build"`
Expected: builds with no errors.

- [ ] **Step 6: Manual verification against the running API**

Start the API if not already running (check `docker ps`/existing `dotnet run` process first — a `eliferpg-core_devcontainer-workspace-1` container is up; if `src/Api` isn't already listening on 5100, start it: `docker exec -d eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet run --project src/Api/Api.csproj"`, then wait a few seconds).

```bash
GAMESERVER_TOKEN=$(curl -s -X POST http://localhost:8180/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=gameserver-dev" -d "client_secret=dev-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
STAFF_TOKEN=$(curl -s -X POST http://localhost:8180/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

# Turn whitelist on for the dev server
curl -s -X PATCH http://localhost:5100/api/game-servers/gameserver-dev \
  -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" -d '{"whitelistEnabled": true}'

# Bootstrap a session — expect status not_whitelisted
BOHEMIA_ID=$(python3 -c "import uuid; print(uuid.uuid4())")
curl -s -X POST http://localhost:5100/api/accounts/session-bootstrap \
  -H "Authorization: Bearer $GAMESERVER_TOKEN" -H "Content-Type: application/json" \
  -d "{\"bohemiaId\": \"$BOHEMIA_ID\"}"
```

Expected: `{"accountId": "...", "keycloakUsername": "...", "status": "not_whitelisted"}`. Take the returned `accountId` and submit + approve an application, then re-run `session-bootstrap` and confirm `status` becomes `"active"`.

- [ ] **Step 7: Commit**

```bash
git add src/Accounts/Accounts.Api src/Api/Program.cs
git commit -m "feat(accounts): add whitelist application and game server HTTP endpoints"
```

---

## Task 8: Bridge repo — generalize the token-exchange gate and add the submission proxy

**Repo:** `/home/kevin/dev/projects/eliferpg-reforger-bridge` (separate from this one — see `master`'s `eea4b0c`).

**Files:**
- Modify: `src/Bridge.Host/BridgeTokenProvider.cs`
- Modify: `src/Bridge.Host/SessionLocalEndpoints.cs`
- Regenerate: `src/Bridge.ApiClient/Generated/**` (via `scripts/generate-bridge-client.sh`, which reads the Central API's live `/openapi/v1.json` — Task 7 must already be running locally on `:5100` for this to pick up the new `api/whitelist-applications` endpoint)
- Create: `src/Bridge.Host/WhitelistLocalEndpoints.cs`

- [ ] **Step 1: Generalize the status check in `BridgeTokenProvider.cs`**

Change:

```csharp
public async Task<PlayerToken?> ExchangeForPlayerTokenAsync(string keycloakUsername, string status, CancellationToken cancellationToken = default)
{
    if (status == "blocked")
    {
        return null;
    }
```

to:

```csharp
public async Task<PlayerToken?> ExchangeForPlayerTokenAsync(string keycloakUsername, string status, CancellationToken cancellationToken = default)
{
    if (status != "active")
    {
        return null;
    }
```

- [ ] **Step 2: Regenerate the Bridge API client**

With the Central API running locally with Task 7's changes (`dotnet run --project src/Api/Api.csproj` in `eliferpg-core`, port 5100):

```bash
cd /home/kevin/dev/projects/eliferpg-reforger-bridge
./scripts/generate-bridge-client.sh
```

Expected: new generated files under `src/Bridge.ApiClient/Generated/Api/WhitelistApplications/`.

- [ ] **Step 3: Add a lookup method to `PlayerSessionTracker`**

`PlayerSessionTracker` (confirmed by reading `src/Bridge.Host/PlayerSessionTracker.cs`) currently only exposes `Start`/`SetActiveCharacter`/`End` — no way to look up a connected player's `AccountId` without removing their session. Add a non-destructive lookup:

```csharp
// in PlayerSessionTracker, alongside the existing Start/SetActiveCharacter/End
public PlayerSession? Get(Guid bohemiaId) => _sessions.TryGetValue(bohemiaId, out var session) ? session : null;
```

- [ ] **Step 4: Add the `submit-whitelist-application` local endpoint**

```csharp
// src/Bridge.Host/WhitelistLocalEndpoints.cs
using ELifeRPG.Bridge.ApiClient;
using ApiModels = ELifeRPG.Bridge.ApiClient.Models;

namespace ELifeRPG.Bridge.Host;

public static class WhitelistLocalEndpoints
{
    public static WebApplication MapWhitelistLocalEndpoints(this WebApplication app)
    {
        app.MapPost("submit-whitelist-application", async (
                SubmitWhitelistApplicationRequest request,
                EliferpgApiClient apiClient,
                PlayerSessionTracker sessions,
                CancellationToken cancellationToken) =>
            {
                var session = sessions.Get(request.BohemiaId);
                if (session is null)
                {
                    return Results.Problem("No active session for this Bohemia ID.", statusCode: StatusCodes.Status404NotFound);
                }

                try
                {
                    var result = await apiClient.Api.WhitelistApplications.PostAsync(
                        new ApiModels.SubmitWhitelistApplicationRequestDto
                        {
                            AccountId = session.AccountId,
                            ApplicationText = request.ApplicationText,
                        },
                        cancellationToken: cancellationToken);
                    return Results.Ok(result);
                }
                catch (ApiModels.ProblemDetails problem)
                {
                    return Results.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.ResponseStatusCode);
                }
            })
            .WithName("SubmitWhitelistApplication")
            .WithDescription("Local-only: submits the connected player's whitelist application for this server.");

        return app;
    }
}

public sealed record SubmitWhitelistApplicationRequest(Guid BohemiaId, string ApplicationText);
```

- [ ] **Step 5: Wire the new endpoint group into `Program.cs`**

Find the existing `app.MapSessionLocalEndpoints();`-style call chain in `src/Bridge.Host/Program.cs` and add `app.MapWhitelistLocalEndpoints();` alongside it.

- [ ] **Step 6: Build**

Run: `docker exec <bridge-devcontainer-name> bash -lc "cd /workspace && dotnet build"` — confirm the Bridge repo's own devcontainer/container name first (`docker ps`, look for something bridge-related; it will differ from `eliferpg-core_devcontainer-workspace-1`). If no devcontainer is running for this repo, start one per its own README before this step.
Expected: builds with no errors.

- [ ] **Step 7: Manual verification**

With both `src/Api` (eliferpg-core) and `src/Bridge.Host` (eliferpg-reforger-bridge) running locally, exercise `player-connected` for a `not_whitelisted` account and confirm no token comes back, then approve the application and confirm a reconnect gets one — mirrors the spec's Testing section.

- [ ] **Step 8: Commit**

```bash
git add src/Bridge.Host src/Bridge.ApiClient
git commit -m "feat: gate player-connected token issuance on whitelist status; add submit-whitelist-application proxy"
```

---

## Task 9: Final verification and cleanup

- [ ] **Step 1: Full test suite, eliferpg-core**

Run: `docker exec eliferpg-core_devcontainer-workspace-1 bash -lc "cd /workspace && dotnet build && dotnet test"`
Expected: all projects build, all tests pass (existing suites plus everything added in Tasks 1–7).

- [ ] **Step 2: Full test suite, eliferpg-reforger-bridge**

Run the Bridge repo's own build/test commands (check its README for the exact invocation — it may not have an xunit test project at all yet; a clean `dotnet build` is the minimum bar).

- [ ] **Step 3: Merge `feature/player-whitelist` into `master` (eliferpg-core)**

```bash
git -C /home/kevin/dev/projects/eliferpg-core checkout master
git -C /home/kevin/dev/projects/eliferpg-core merge feature/player-whitelist --no-edit
```

- [ ] **Step 4: Merge the Bridge repo's whitelist branch into its `master`**

(If Task 8 was done on a feature branch rather than directly on `master` — match whichever convention was actually used when Task 8 ran.)

- [ ] **Step 5: Remove the original spec-authoring worktree**

The spec was authored in `~/.herdr/worktrees/eliferpg-core/worktree-green-stone-19b3` on branch `worktree/green-stone-19b3`, already fully merged into `master` (commits `be6b2bc`, `5b0fe51`, `499c4c1`). Once Step 3 confirms `master` has everything:

```bash
git -C /home/kevin/dev/projects/eliferpg-core worktree remove /home/kevin/.herdr/worktrees/eliferpg-core/worktree-green-stone-19b3
git -C /home/kevin/dev/projects/eliferpg-core branch -d worktree/green-stone-19b3
git -C /home/kevin/dev/projects/eliferpg-core branch -d feature/player-whitelist
```
