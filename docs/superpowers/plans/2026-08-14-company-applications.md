# Company Applications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a character apply to a company with a free-text message, and let members with `ManageMembers` permission list, confirm, accept, or deny those applications — accepting grants the applicant the company's default position.

**Architecture:** Extends the existing event-sourced `Company` aggregate (Marten, single stream per company) with an embedded `Applications` list, following the exact same "domain method mutates + returns event" pattern `AddMember` already uses. Application-layer commands/query follow the existing `union`-result + `IRequestHandler` convention. New endpoints reuse the existing `CompaniesWritePolicy` scope-based auth, with a new in-process `CompanyMemberAuthorization` helper enforcing `CompanyPermissions.ManageMembers` for the management actions — the first real enforcement of that flag within the `Companies` module.

**Tech Stack:** .NET 11 preview (C# preview, native discriminated unions via `union`), Marten 9.23 (event sourcing), Mediator 3.0.2 (source-generated CQRS, not MediatR), StronglyTypedId 1.0.0-beta08, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-14-company-applications-design.md`

## Global Constraints

- Free-text application message max length: 1000 characters, enforced manually at the API endpoint (400 Bad Request on violation) — no attribute-based validation pipeline exists in this codebase.
- Accept/Deny are valid from `Pending` or `InProgress` (not gated behind Confirm). Confirm is only valid from `Pending`.
- Reapplying after a `Denied` application is allowed; only one open (`Pending`/`InProgress`) application per character per company at a time.
- Accepting an application always grants the company's default (lowest-ranked/highest-`Ordering`) position — no explicit position selection.
- `CompanyDetailsDto`/`GET companies/{companyId}` must NOT be extended with applications (no `ManageMembers` gate there today).
- All new types/namespaces follow existing module conventions exactly (file-per-type, `sealed record`, explicit `using`s for `.Events`/`.Exceptions`/`.Common` sub-namespaces even though the root namespace is globally used).

---

## Task 1: Domain foundation — types, events, exceptions

**Files:**
- Create: `src/Companies/Companies.Domain/CompanyApplicationId.cs`
- Create: `src/Companies/Companies.Domain/CompanyApplicationStatus.cs`
- Create: `src/Companies/Companies.Domain/CompanyApplication.cs`
- Create: `src/Companies/Companies.Domain/Events/ApplicationSubmitted.cs`
- Create: `src/Companies/Companies.Domain/Events/ApplicationConfirmed.cs`
- Create: `src/Companies/Companies.Domain/Events/ApplicationAccepted.cs`
- Create: `src/Companies/Companies.Domain/Events/ApplicationDenied.cs`
- Create: `src/Companies/Companies.Domain/Exceptions/DuplicateApplicationException.cs`
- Create: `src/Companies/Companies.Domain/Exceptions/ApplicationNotFoundException.cs`
- Create: `src/Companies/Companies.Domain/Exceptions/InvalidApplicationStateException.cs`

**Interfaces:**
- Consumes: nothing new — `CompanyId`, `CharacterId` from `ELifeRPG.Shared.Kernel` (already global-used in this project).
- Produces: `CompanyApplicationId` (strongly-typed id), `CompanyApplicationStatus` enum (`Pending`, `InProgress`, `Accepted`, `Denied`), `CompanyApplication` record `(CompanyApplicationId Id, CharacterId CharacterId, string Message, CompanyApplicationStatus Status)`, four event records, three exception types — all consumed by Task 2-5.

These are pure data types with no behavior to unit-test in isolation (matching how `CompanyPositionId`/`CompanyMembership`/`CompanyCreated` have no dedicated tests either) — this task is verified by a successful build, and exercised indirectly by Task 2's tests.

- [ ] **Step 1: Create `CompanyApplicationId.cs`**

```csharp
namespace ELifeRPG.Companies.Domain;

[StronglyTypedId]
public partial struct CompanyApplicationId;
```

- [ ] **Step 2: Create `CompanyApplicationStatus.cs`**

```csharp
namespace ELifeRPG.Companies.Domain;

public enum CompanyApplicationStatus
{
    Pending,
    InProgress,
    Accepted,
    Denied,
}
```

- [ ] **Step 3: Create `CompanyApplication.cs`**

```csharp
namespace ELifeRPG.Companies.Domain;

public sealed record CompanyApplication(CompanyApplicationId Id, CharacterId CharacterId, string Message, CompanyApplicationStatus Status);
```

- [ ] **Step 4: Create the four event records**

`src/Companies/Companies.Domain/Events/ApplicationSubmitted.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationSubmitted(CompanyId Id, CompanyApplicationId ApplicationId, CharacterId CharacterId, string Message);
```

`src/Companies/Companies.Domain/Events/ApplicationConfirmed.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationConfirmed(CompanyId Id, CompanyApplicationId ApplicationId);
```

`src/Companies/Companies.Domain/Events/ApplicationAccepted.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationAccepted(CompanyId Id, CompanyApplicationId ApplicationId);
```

`src/Companies/Companies.Domain/Events/ApplicationDenied.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationDenied(CompanyId Id, CompanyApplicationId ApplicationId);
```

- [ ] **Step 5: Create the three exception types**

`src/Companies/Companies.Domain/Exceptions/DuplicateApplicationException.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Exceptions;

/// <summary>Thrown by Company.SubmitApplication when the character already has an open
/// (Pending/InProgress) application to this company. Reapplying after a Denied application is allowed.</summary>
public sealed class DuplicateApplicationException(string message) : InvalidOperationException(message);
```

`src/Companies/Companies.Domain/Exceptions/ApplicationNotFoundException.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Exceptions;

public sealed class ApplicationNotFoundException(string message) : InvalidOperationException(message);
```

`src/Companies/Companies.Domain/Exceptions/InvalidApplicationStateException.cs`:
```csharp
namespace ELifeRPG.Companies.Domain.Exceptions;

public sealed class InvalidApplicationStateException(string message) : InvalidOperationException(message);
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/Companies/Companies.Domain/Companies.Domain.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Companies/Companies.Domain/CompanyApplicationId.cs \
        src/Companies/Companies.Domain/CompanyApplicationStatus.cs \
        src/Companies/Companies.Domain/CompanyApplication.cs \
        src/Companies/Companies.Domain/Events/ApplicationSubmitted.cs \
        src/Companies/Companies.Domain/Events/ApplicationConfirmed.cs \
        src/Companies/Companies.Domain/Events/ApplicationAccepted.cs \
        src/Companies/Companies.Domain/Events/ApplicationDenied.cs \
        src/Companies/Companies.Domain/Exceptions/DuplicateApplicationException.cs \
        src/Companies/Companies.Domain/Exceptions/ApplicationNotFoundException.cs \
        src/Companies/Companies.Domain/Exceptions/InvalidApplicationStateException.cs
git commit -m "Add company application domain types, events, and exceptions"
```

---

## Task 2: Company.SubmitApplication

**Files:**
- Modify: `src/Companies/Companies.Domain/Company.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`
- Test: `tests/Companies.Domain.UnitTests/CompanyTests.cs`

**Interfaces:**
- Consumes: `CompanyApplication`, `CompanyApplicationStatus`, `ApplicationSubmitted`, `DuplicateApplicationException` from Task 1; existing `AlreadyMemberException`.
- Produces: `Company.Applications` (`List<CompanyApplication>`), `Company.SubmitApplication(CharacterId, string) : ApplicationSubmitted`, `Company.Apply(ApplicationSubmitted)` — all consumed by Tasks 3-8.

- [ ] **Step 1: Write the failing tests in `CompanyTests.cs`**

Add these `[Fact]` methods to the `CompanyTests` class (after `Apply_ReplayingCreatedThenMemberAdded_ResultsInSameMembership`):

```csharp
    [Fact]
    public void SubmitApplication_ForNewApplicant_AddsPendingApplication()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());

        var @event = company.SubmitApplication(characterId, "Please let me in.");

        Assert.Equal(characterId, @event.CharacterId);
        Assert.Equal("Please let me in.", @event.Message);
        var application = Assert.Single(company.Applications);
        Assert.Equal(@event.ApplicationId, application.Id);
        Assert.Equal(CompanyApplicationStatus.Pending, application.Status);
    }

    [Fact]
    public void SubmitApplication_ForExistingMember_ThrowsAlreadyMember()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        company.AddMember(characterId);

        Assert.Throws<AlreadyMemberException>(() => company.SubmitApplication(characterId, "Let me in again."));
    }

    [Fact]
    public void SubmitApplication_WithExistingOpenApplication_ThrowsDuplicateApplication()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        company.SubmitApplication(characterId, "First try.");

        Assert.Throws<DuplicateApplicationException>(() => company.SubmitApplication(characterId, "Second try."));
    }

    [Fact]
    public void Apply_ReplayingApplicationSubmitted_ResultsInSamePendingApplication()
    {
        var companyId = new CompanyId(Guid.NewGuid());
        var ownerPositionId = new CompanyPositionId(Guid.NewGuid());
        var defaultPositionId = new CompanyPositionId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());
        var applicationId = new CompanyApplicationId(Guid.NewGuid());

        var company = Company.Create(new CompanyCreated(companyId, "Acme Corp", ownerPositionId, defaultPositionId));
        company.Apply(new ApplicationSubmitted(companyId, applicationId, characterId, "Hire me."));

        Assert.Single(company.Applications);
        Assert.Equal(characterId, company.Applications[0].CharacterId);
        Assert.Equal(CompanyApplicationStatus.Pending, company.Applications[0].Status);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~SubmitApplication|FullyQualifiedName~ApplicationSubmitted"`
Expected: FAIL (compile error — `Company.SubmitApplication`/`Company.Applications`/`Company.Apply(ApplicationSubmitted)` don't exist yet).

- [ ] **Step 3: Implement `Company.SubmitApplication` in `Company.cs`**

Add `using ELifeRPG.Companies.Domain.Events;` (already present) — no new using needed since `Exceptions` is already imported.

Add the property, right after the `Memberships` property (line 19):

```csharp
    [JsonInclude]
    public List<CompanyApplication> Applications { get; private set; } = [];
```

Add the method, right after `AddMember` (after its closing brace, before the `Apply(CompanyCreated @event)` method):

```csharp
    public ApplicationSubmitted SubmitApplication(CharacterId characterId, string message)
    {
        if (Memberships.Any(x => x.CharacterId == characterId))
        {
            throw new AlreadyMemberException("Character is already a member of this company.");
        }

        if (Applications.Any(x => x.CharacterId == characterId && x.Status is CompanyApplicationStatus.Pending or CompanyApplicationStatus.InProgress))
        {
            throw new DuplicateApplicationException("Character already has an open application to this company.");
        }

        var @event = new ApplicationSubmitted(Id, new CompanyApplicationId(Guid.NewGuid()), characterId, message);
        Apply(@event);
        return @event;
    }
```

Add the `Apply` overload, right after `Apply(MemberAdded @event)`:

```csharp
    public void Apply(ApplicationSubmitted @event) =>
        Applications.Add(new CompanyApplication(@event.ApplicationId, @event.CharacterId, @event.Message, CompanyApplicationStatus.Pending));
```

- [ ] **Step 4: Register the projection**

In `CompanyProjection.cs`, add after `Apply(Company company, MemberAdded @event)`:

```csharp
    public void Apply(Company company, ApplicationSubmitted @event) => company.Apply(@event);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj`
Expected: PASS, all tests (old and new) green.

- [ ] **Step 6: Commit**

```bash
git add src/Companies/Companies.Domain/Company.cs \
        src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs \
        tests/Companies.Domain.UnitTests/CompanyTests.cs
git commit -m "Add Company.SubmitApplication"
```

---

## Task 3: Company.ConfirmApplication

**Files:**
- Modify: `src/Companies/Companies.Domain/Company.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`
- Test: `tests/Companies.Domain.UnitTests/CompanyTests.cs`

**Interfaces:**
- Consumes: `ApplicationNotFoundException`, `InvalidApplicationStateException` from Task 1; `Company.Applications`/`SubmitApplication` from Task 2.
- Produces: `Company.ConfirmApplication(CompanyApplicationId) : ApplicationConfirmed`, `Company.Apply(ApplicationConfirmed)` — consumed by Task 6/8.

- [ ] **Step 1: Write the failing tests in `CompanyTests.cs`**

Add after the Task 2 tests:

```csharp
    [Fact]
    public void ConfirmApplication_ForPendingApplication_SetsInProgress()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var @event = company.ConfirmApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, @event.ApplicationId);
        Assert.Equal(CompanyApplicationStatus.InProgress, company.Applications.Single().Status);
    }

    [Fact]
    public void ConfirmApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.ConfirmApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void ConfirmApplication_ForAlreadyConfirmedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.ConfirmApplication(submitted.ApplicationId));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~ConfirmApplication"`
Expected: FAIL (compile error — `Company.ConfirmApplication` doesn't exist yet).

- [ ] **Step 3: Implement `Company.ConfirmApplication` in `Company.cs`**

Add after `SubmitApplication`:

```csharp
    public ApplicationConfirmed ConfirmApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status != CompanyApplicationStatus.Pending)
        {
            throw new InvalidApplicationStateException("Application must be Pending to be confirmed.");
        }

        var @event = new ApplicationConfirmed(Id, applicationId);
        Apply(@event);
        return @event;
    }
```

Add the `Apply` overload after `Apply(ApplicationSubmitted @event)`:

```csharp
    public void Apply(ApplicationConfirmed @event)
    {
        var index = Applications.FindIndex(x => x.Id == @event.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.InProgress };
    }
```

- [ ] **Step 4: Register the projection**

In `CompanyProjection.cs`, add after `Apply(Company company, ApplicationSubmitted @event)`:

```csharp
    public void Apply(Company company, ApplicationConfirmed @event) => company.Apply(@event);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj`
Expected: PASS, all tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Companies/Companies.Domain/Company.cs \
        src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs \
        tests/Companies.Domain.UnitTests/CompanyTests.cs
git commit -m "Add Company.ConfirmApplication"
```

---

## Task 4: Company.AcceptApplication

**Files:**
- Modify: `src/Companies/Companies.Domain/Company.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`
- Test: `tests/Companies.Domain.UnitTests/CompanyTests.cs`

**Interfaces:**
- Consumes: `ApplicationAccepted` from Task 1, `Company.AddMember` (existing), `Company.Applications`/`ConfirmApplication` from Tasks 2-3.
- Produces: `Company.AcceptApplication(CompanyApplicationId) : (ApplicationAccepted AcceptedEvent, MemberAdded MemberAddedEvent)`, `Company.Apply(ApplicationAccepted)` — consumed by Task 6/8.

- [ ] **Step 1: Write the failing tests in `CompanyTests.cs`**

Add after the Task 3 tests:

```csharp
    [Fact]
    public void AcceptApplication_ForPendingApplication_GrantsDefaultPosition()
    {
        var company = CreateCompany(out _, out var defaultPositionId);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var (acceptedEvent, memberAddedEvent) = company.AcceptApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, acceptedEvent.ApplicationId);
        Assert.Equal(defaultPositionId, memberAddedEvent.PositionId);
        Assert.Equal(CompanyApplicationStatus.Accepted, company.Applications.Single().Status);
        Assert.Contains(company.Memberships, m => m.CharacterId == submitted.CharacterId && m.PositionId == defaultPositionId);
    }

    [Fact]
    public void AcceptApplication_ForInProgressApplication_GrantsDefaultPosition()
    {
        var company = CreateCompany(out _, out var defaultPositionId);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        var (_, memberAddedEvent) = company.AcceptApplication(submitted.ApplicationId);

        Assert.Equal(defaultPositionId, memberAddedEvent.PositionId);
    }

    [Fact]
    public void AcceptApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.AcceptApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void AcceptApplication_ForAlreadyAcceptedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.AcceptApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.AcceptApplication(submitted.ApplicationId));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~AcceptApplication"`
Expected: FAIL (compile error — `Company.AcceptApplication` doesn't exist yet).

- [ ] **Step 3: Implement `Company.AcceptApplication` in `Company.cs`**

Add after `ConfirmApplication`:

```csharp
    public (ApplicationAccepted AcceptedEvent, MemberAdded MemberAddedEvent) AcceptApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status is CompanyApplicationStatus.Accepted or CompanyApplicationStatus.Denied)
        {
            throw new InvalidApplicationStateException("Application has already been decided.");
        }

        var acceptedEvent = new ApplicationAccepted(Id, applicationId);
        Apply(acceptedEvent);
        var memberAddedEvent = AddMember(application.CharacterId);

        return (acceptedEvent, memberAddedEvent);
    }
```

Add the `Apply` overload after `Apply(ApplicationConfirmed @event)`:

```csharp
    public void Apply(ApplicationAccepted @event)
    {
        var index = Applications.FindIndex(x => x.Id == @event.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.Accepted };
    }
```

- [ ] **Step 4: Register the projection**

In `CompanyProjection.cs`, add after `Apply(Company company, ApplicationConfirmed @event)`:

```csharp
    public void Apply(Company company, ApplicationAccepted @event) => company.Apply(@event);
```

(`MemberAdded` is already registered from the existing `AddMember` flow — no separate registration needed.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj`
Expected: PASS, all tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Companies/Companies.Domain/Company.cs \
        src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs \
        tests/Companies.Domain.UnitTests/CompanyTests.cs
git commit -m "Add Company.AcceptApplication"
```

---

## Task 5: Company.DenyApplication

**Files:**
- Modify: `src/Companies/Companies.Domain/Company.cs`
- Modify: `src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs`
- Test: `tests/Companies.Domain.UnitTests/CompanyTests.cs`

**Interfaces:**
- Consumes: `ApplicationDenied` from Task 1, `Company.Applications`/`AcceptApplication` from Tasks 2-4.
- Produces: `Company.DenyApplication(CompanyApplicationId) : ApplicationDenied`, `Company.Apply(ApplicationDenied)` — consumed by Task 6/8.

- [ ] **Step 1: Write the failing tests in `CompanyTests.cs`**

Add after the Task 4 tests:

```csharp
    [Fact]
    public void DenyApplication_ForPendingApplication_SetsDenied()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var @event = company.DenyApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, @event.ApplicationId);
        Assert.Equal(CompanyApplicationStatus.Denied, company.Applications.Single().Status);
    }

    [Fact]
    public void DenyApplication_ForInProgressApplication_SetsDenied()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        company.DenyApplication(submitted.ApplicationId);

        Assert.Equal(CompanyApplicationStatus.Denied, company.Applications.Single().Status);
    }

    [Fact]
    public void DenyApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.DenyApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void DenyApplication_ForAlreadyDeniedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.DenyApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.DenyApplication(submitted.ApplicationId));
    }

    [Fact]
    public void AcceptApplication_ForAlreadyDeniedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.DenyApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.AcceptApplication(submitted.ApplicationId));
    }

    [Fact]
    public void SubmitApplication_AfterPriorDenial_Succeeds()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        var firstSubmission = company.SubmitApplication(characterId, "First try.");
        company.DenyApplication(firstSubmission.ApplicationId);

        var secondSubmission = company.SubmitApplication(characterId, "Second try.");

        Assert.Equal(2, company.Applications.Count);
        Assert.Equal(CompanyApplicationStatus.Pending, company.Applications.Single(a => a.Id == secondSubmission.ApplicationId).Status);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj --filter "FullyQualifiedName~DenyApplication|FullyQualifiedName~AfterPriorDenial"`
Expected: FAIL (compile error — `Company.DenyApplication` doesn't exist yet).

- [ ] **Step 3: Implement `Company.DenyApplication` in `Company.cs`**

Add after `AcceptApplication`:

```csharp
    public ApplicationDenied DenyApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status is CompanyApplicationStatus.Accepted or CompanyApplicationStatus.Denied)
        {
            throw new InvalidApplicationStateException("Application has already been decided.");
        }

        var @event = new ApplicationDenied(Id, applicationId);
        Apply(@event);
        return @event;
    }
```

Add the `Apply` overload after `Apply(ApplicationAccepted @event)`:

```csharp
    public void Apply(ApplicationDenied @event)
    {
        var index = Applications.FindIndex(x => x.Id == @event.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.Denied };
    }
```

- [ ] **Step 4: Register the projection**

In `CompanyProjection.cs`, add after `Apply(Company company, ApplicationAccepted @event)`:

```csharp
    public void Apply(Company company, ApplicationDenied @event) => company.Apply(@event);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj`
Expected: PASS, all tests green (this is the full domain layer for the feature — every test added in Tasks 2-5 should now pass together).

- [ ] **Step 6: Commit**

```bash
git add src/Companies/Companies.Domain/Company.cs \
        src/Companies/Companies.Infrastructure/Common/CompanyProjection.cs \
        tests/Companies.Domain.UnitTests/CompanyTests.cs
git commit -m "Add Company.DenyApplication"
```

---

## Task 6: Application layer — SubmitApplicationCommand

**Files:**
- Create: `src/Companies/Companies.Application/Companies/SubmitApplicationCommand.cs`

**Interfaces:**
- Consumes: `Company.SubmitApplication` (Task 2), `ICompanyRepository` (existing), `CharacterLookupQuery`/`CharacterLookupResult` from `ELifeRPG.Characters.Application.Characters` (existing, same as `AddMemberCommand` uses).
- Produces: `SubmitApplicationCommand(CompanyId, CharacterId, string) : IRequest<SubmitApplicationResult>`, `union SubmitApplicationResult(Submitted(CompanyApplicationId ApplicationId), CompanyNotFound, CharacterNotFound, AlreadyMember, DuplicateApplication)` — consumed by Task 9 (API) and Task 10 (integration tests).

No dedicated unit test for this task: this codebase has no Application-layer unit test project for `Companies` (`AddMemberCommand`/`CreateCompanyCommand` are covered only by `Companies.IntegrationTests`, which needs the local infra stack) — Task 10 covers this handler end-to-end. This task is verified by a successful build.

- [ ] **Step 1: Create `SubmitApplicationCommand.cs`**

```csharp
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union SubmitApplicationResult(
    SubmitApplicationResult.Submitted,
    SubmitApplicationResult.CompanyNotFound,
    SubmitApplicationResult.CharacterNotFound,
    SubmitApplicationResult.AlreadyMember,
    SubmitApplicationResult.DuplicateApplication)
{
    public record Submitted(CompanyApplicationId ApplicationId);

    public record CompanyNotFound;

    public record CharacterNotFound;

    public record AlreadyMember;

    public record DuplicateApplication;
}

public sealed record SubmitApplicationCommand(CompanyId CompanyId, CharacterId CharacterId, string Message) : IRequest<SubmitApplicationResult>;

public sealed class SubmitApplicationHandler(ICompanyRepository companyRepository, IMediator mediator)
    : IRequestHandler<SubmitApplicationCommand, SubmitApplicationResult>
{
    public async ValueTask<SubmitApplicationResult> Handle(SubmitApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new SubmitApplicationResult.CompanyNotFound();
        }

        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.CharacterId), cancellationToken);
        if (characterLookup is CharacterLookupResult.NotFound)
        {
            return new SubmitApplicationResult.CharacterNotFound();
        }

        ApplicationSubmitted @event;
        try
        {
            @event = company.SubmitApplication(request.CharacterId, request.Message);
        }
        catch (AlreadyMemberException)
        {
            return new SubmitApplicationResult.AlreadyMember();
        }
        catch (DuplicateApplicationException)
        {
            return new SubmitApplicationResult.DuplicateApplication();
        }

        companyRepository.Append(request.CompanyId, @event);
        await companyRepository.SaveChangesAsync(cancellationToken);

        return new SubmitApplicationResult.Submitted(@event.ApplicationId);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Companies/Companies.Application/Companies.Application.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Companies/Companies.Application/Companies/SubmitApplicationCommand.cs
git commit -m "Add SubmitApplicationCommand"
```

---

## Task 7: Application layer — CompanyMemberAuthorization + CompanyApplicationsQuery

**Files:**
- Create: `src/Companies/Companies.Application/Common/CompanyMemberAuthorization.cs`
- Create: `src/Companies/Companies.Application/Companies/CompanyApplicationsQuery.cs`

**Interfaces:**
- Consumes: `Company.Memberships`/`Positions`/`Applications` (existing/Task 2), `CompanyPermissions.ManageMembers` (existing).
- Produces: `CompanyMemberAuthorization.CanManageMembers(Company, CharacterId) : bool` — consumed by this task's query and Task 8's three commands. `CompanyApplicationsQuery(CompanyId, CharacterId ActingCharacterId) : IRequest<CompanyApplicationsResult>`, `union CompanyApplicationsResult(Found(IReadOnlyList<CompanyApplication> Applications), CompanyNotFound, NotAuthorized)` — consumed by Task 9/10.

No dedicated unit test — same reasoning as Task 6; covered by Task 10's integration tests.

- [ ] **Step 1: Create `CompanyMemberAuthorization.cs`**

```csharp
namespace ELifeRPG.Companies.Application.Common;

/// <summary>
/// Checks whether a character can manage members/applications for a company, using an
/// already-loaded Company aggregate. Unlike CompanyMemberPermissionsQuery (the cross-module surface
/// other modules like Banking use — see ARCHITECTURE.md §9e), this stays in-process:
/// Companies.Application handlers already have the aggregate loaded, so a second mediator
/// round-trip would be redundant.
/// </summary>
internal static class CompanyMemberAuthorization
{
    public static bool CanManageMembers(Company company, CharacterId characterId)
    {
        var membership = company.Memberships.SingleOrDefault(x => x.CharacterId == characterId);
        if (membership is null)
        {
            return false;
        }

        var position = company.Positions.Single(x => x.Id == membership.PositionId);
        return position.Permissions.HasFlag(CompanyPermissions.ManageMembers);
    }
}
```

- [ ] **Step 2: Create `CompanyApplicationsQuery.cs`**

```csharp
using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

public union CompanyApplicationsResult(CompanyApplicationsResult.Found, CompanyApplicationsResult.CompanyNotFound, CompanyApplicationsResult.NotAuthorized)
{
    public record Found(IReadOnlyList<CompanyApplication> Applications);

    public record CompanyNotFound;

    public record NotAuthorized;
}

public sealed record CompanyApplicationsQuery(CompanyId CompanyId, CharacterId ActingCharacterId) : IRequest<CompanyApplicationsResult>;

public sealed class CompanyApplicationsHandler(ICompanyRepository companyRepository)
    : IRequestHandler<CompanyApplicationsQuery, CompanyApplicationsResult>
{
    public async ValueTask<CompanyApplicationsResult> Handle(CompanyApplicationsQuery request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new CompanyApplicationsResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new CompanyApplicationsResult.NotAuthorized();
        }

        return new CompanyApplicationsResult.Found(company.Applications);
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Companies/Companies.Application/Companies.Application.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Companies/Companies.Application/Common/CompanyMemberAuthorization.cs \
        src/Companies/Companies.Application/Companies/CompanyApplicationsQuery.cs
git commit -m "Add CompanyMemberAuthorization and CompanyApplicationsQuery"
```

---

## Task 8: Application layer — Confirm/Accept/DenyApplicationCommand

**Files:**
- Create: `src/Companies/Companies.Application/Companies/ConfirmApplicationCommand.cs`
- Create: `src/Companies/Companies.Application/Companies/AcceptApplicationCommand.cs`
- Create: `src/Companies/Companies.Application/Companies/DenyApplicationCommand.cs`

**Interfaces:**
- Consumes: `Company.ConfirmApplication`/`AcceptApplication`/`DenyApplication` (Tasks 3-5), `CompanyMemberAuthorization.CanManageMembers` (Task 7), `ApplicationNotFoundException`/`InvalidApplicationStateException` (Task 1).
- Produces: `ConfirmApplicationCommand`/`AcceptApplicationCommand`/`DenyApplicationCommand`, each `(CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId) : IRequest<TResult>`, with matching `union` results (`Confirmed`/`Accepted`/`Denied`, `CompanyNotFound`, `NotAuthorized`, `ApplicationNotFound`, `InvalidState`) — consumed by Task 9/10.

No dedicated unit test — same reasoning as Task 6; covered by Task 10's integration tests.

- [ ] **Step 1: Create `ConfirmApplicationCommand.cs`**

```csharp
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union ConfirmApplicationResult(
    ConfirmApplicationResult.Confirmed,
    ConfirmApplicationResult.CompanyNotFound,
    ConfirmApplicationResult.NotAuthorized,
    ConfirmApplicationResult.ApplicationNotFound,
    ConfirmApplicationResult.InvalidState)
{
    public record Confirmed;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;
}

public sealed record ConfirmApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<ConfirmApplicationResult>;

public sealed class ConfirmApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<ConfirmApplicationCommand, ConfirmApplicationResult>
{
    public async ValueTask<ConfirmApplicationResult> Handle(ConfirmApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new ConfirmApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new ConfirmApplicationResult.NotAuthorized();
        }

        try
        {
            var @event = company.ConfirmApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, @event);
        }
        catch (ApplicationNotFoundException)
        {
            return new ConfirmApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new ConfirmApplicationResult.InvalidState();
        }

        await companyRepository.SaveChangesAsync(cancellationToken);

        return new ConfirmApplicationResult.Confirmed();
    }
}
```

- [ ] **Step 2: Create `AcceptApplicationCommand.cs`**

```csharp
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union AcceptApplicationResult(
    AcceptApplicationResult.Accepted,
    AcceptApplicationResult.CompanyNotFound,
    AcceptApplicationResult.NotAuthorized,
    AcceptApplicationResult.ApplicationNotFound,
    AcceptApplicationResult.InvalidState)
{
    public record Accepted;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;
}

public sealed record AcceptApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<AcceptApplicationResult>;

public sealed class AcceptApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<AcceptApplicationCommand, AcceptApplicationResult>
{
    public async ValueTask<AcceptApplicationResult> Handle(AcceptApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new AcceptApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new AcceptApplicationResult.NotAuthorized();
        }

        try
        {
            var (acceptedEvent, memberAddedEvent) = company.AcceptApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, acceptedEvent);
            companyRepository.Append(request.CompanyId, memberAddedEvent);
        }
        catch (ApplicationNotFoundException)
        {
            return new AcceptApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new AcceptApplicationResult.InvalidState();
        }

        await companyRepository.SaveChangesAsync(cancellationToken);

        return new AcceptApplicationResult.Accepted();
    }
}
```

- [ ] **Step 3: Create `DenyApplicationCommand.cs`**

```csharp
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union DenyApplicationResult(
    DenyApplicationResult.Denied,
    DenyApplicationResult.CompanyNotFound,
    DenyApplicationResult.NotAuthorized,
    DenyApplicationResult.ApplicationNotFound,
    DenyApplicationResult.InvalidState)
{
    public record Denied;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;
}

public sealed record DenyApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<DenyApplicationResult>;

public sealed class DenyApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<DenyApplicationCommand, DenyApplicationResult>
{
    public async ValueTask<DenyApplicationResult> Handle(DenyApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new DenyApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new DenyApplicationResult.NotAuthorized();
        }

        try
        {
            var @event = company.DenyApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, @event);
        }
        catch (ApplicationNotFoundException)
        {
            return new DenyApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new DenyApplicationResult.InvalidState();
        }

        await companyRepository.SaveChangesAsync(cancellationToken);

        return new DenyApplicationResult.Denied();
    }
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Companies/Companies.Application/Companies.Application.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Companies/Companies.Application/Companies/ConfirmApplicationCommand.cs \
        src/Companies/Companies.Application/Companies/AcceptApplicationCommand.cs \
        src/Companies/Companies.Application/Companies/DenyApplicationCommand.cs
git commit -m "Add Confirm/Accept/DenyApplicationCommand"
```

---

## Task 9: API layer — DTOs and endpoints

**Files:**
- Create: `src/Companies/Companies.Api/Companies/SubmitApplicationRequestDto.cs`
- Create: `src/Companies/Companies.Api/Companies/CompanyApplicationDto.cs`
- Create: `src/Companies/Companies.Api/Companies/ActingCharacterRequestDto.cs`
- Modify: `src/Companies/Companies.Api/CompanyEndpoints.cs`

**Interfaces:**
- Consumes: `SubmitApplicationCommand`/`Result` (Task 6), `CompanyApplicationsQuery`/`Result` (Task 7), `Confirm/Accept/DenyApplicationCommand`/`Result` (Task 8), `CompanyApplication` (Task 1), `CompaniesWritePolicy` (existing constant on `CompanyModule`).
- Produces: 5 new HTTP endpoints under `CompanyModule` — no other task consumes these directly, but Task 10's assertions rely on the underlying commands/queries, not the HTTP layer.

This codebase has no HTTP-level (`WebApplicationFactory`) test project for any module — endpoints are exercised only via the mediator-level integration tests (Task 10) and manually. This task is verified by a successful build of the API host, which also regenerates `openapi/eliferpg-api-v1.json`.

- [ ] **Step 1: Create `SubmitApplicationRequestDto.cs`**

```csharp
namespace ELifeRPG.Companies.Api.Companies;

public sealed record SubmitApplicationRequestDto
{
    public required Guid CharacterId { get; init; }

    public required string Message { get; init; }

    public SubmitApplicationCommand ToCommand(Guid companyId) => new(new CompanyId(companyId), new CharacterId(CharacterId), Message);
}
```

- [ ] **Step 2: Create `CompanyApplicationDto.cs`**

```csharp
namespace ELifeRPG.Companies.Api.Companies;

public sealed record CompanyApplicationDto
{
    public required Guid ApplicationId { get; init; }

    public required Guid CharacterId { get; init; }

    public required string Message { get; init; }

    public required string Status { get; init; }

    public static CompanyApplicationDto Create(CompanyApplication source) => new()
    {
        ApplicationId = source.Id.Value,
        CharacterId = source.CharacterId.Value,
        Message = source.Message,
        Status = source.Status.ToString(),
    };

    public static CompanyApplicationDto Create(SubmitApplicationResult.Submitted source, SubmitApplicationRequestDto request) => new()
    {
        ApplicationId = source.ApplicationId.Value,
        CharacterId = request.CharacterId,
        Message = request.Message,
        Status = nameof(CompanyApplicationStatus.Pending),
    };
}
```

- [ ] **Step 3: Create `ActingCharacterRequestDto.cs`**

```csharp
namespace ELifeRPG.Companies.Api.Companies;

public sealed record ActingCharacterRequestDto
{
    public required Guid ActingCharacterId { get; init; }

    public ConfirmApplicationCommand ToConfirmCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));

    public AcceptApplicationCommand ToAcceptCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));

    public DenyApplicationCommand ToDenyCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));
}
```

- [ ] **Step 4: Add the 5 endpoints to `CompanyEndpoints.cs`**

In `MapCompanyModule`, insert the following block right before the final `return app;`:

```csharp
        group.MapPost("companies/{companyId:guid}/applications", async (
                Guid companyId,
                [FromBody] SubmitApplicationRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request.Message.Length > 1000)
                {
                    return Results.Problem(
                        title: "Message must be 1000 characters or fewer",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await mediator.Send(request.ToCommand(companyId), cancellationToken);

                return result switch
                {
                    SubmitApplicationResult.Submitted submitted => Results.Ok(CompanyApplicationDto.Create(submitted, request)),
                    SubmitApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitApplicationResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitApplicationResult.AlreadyMember => Results.Problem(
                        title: "Character is already a member",
                        statusCode: StatusCodes.Status409Conflict),
                    SubmitApplicationResult.DuplicateApplication => Results.Problem(
                        title: "Character already has an open application to this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<CompanyApplicationDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SubmitCompanyApplication")
            .WithDescription("Submits a character's application to join a company.");

        group.MapGet("companies/{companyId:guid}/applications", async (
                Guid companyId,
                [FromQuery] Guid actingCharacterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new CompanyApplicationsQuery(new CompanyId(companyId), new CharacterId(actingCharacterId)),
                    cancellationToken);

                return result switch
                {
                    CompanyApplicationsResult.Found found => Results.Ok(found.Applications.Select(CompanyApplicationDto.Create).ToList()),
                    CompanyApplicationsResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    CompanyApplicationsResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<List<CompanyApplicationDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListCompanyApplications")
            .WithDescription("Lists a company's applications. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/confirm", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToConfirmCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    ConfirmApplicationResult.Confirmed => Results.Ok(),
                    ConfirmApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    ConfirmApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    ConfirmApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    ConfirmApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be Pending to be confirmed",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ConfirmCompanyApplication")
            .WithDescription("Marks a pending application as InProgress. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/accept", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToAcceptCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    AcceptApplicationResult.Accepted => Results.Ok(),
                    AcceptApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    AcceptApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    AcceptApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    AcceptApplicationResult.InvalidState => Results.Problem(
                        title: "Application has already been decided",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("AcceptCompanyApplication")
            .WithDescription("Accepts an application, adding the character as a member in the company's default position. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/deny", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToDenyCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    DenyApplicationResult.Denied => Results.Ok(),
                    DenyApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    DenyApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    DenyApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    DenyApplicationResult.InvalidState => Results.Problem(
                        title: "Application has already been decided",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DenyCompanyApplication")
            .WithDescription("Denies an application. Requires ManageMembers permission in the company.");

```

- [ ] **Step 5: Build the API host to verify (also regenerates the OpenAPI doc)**

Run: `dotnet build src/Api/Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Confirm the OpenAPI doc picked up the new endpoints**

Run: `grep -c "CompanyApplication" openapi/eliferpg-api-v1.json`
Expected: A non-zero count (the new DTOs/paths appear in the regenerated spec).

- [ ] **Step 7: Commit**

```bash
git add src/Companies/Companies.Api/Companies/SubmitApplicationRequestDto.cs \
        src/Companies/Companies.Api/Companies/CompanyApplicationDto.cs \
        src/Companies/Companies.Api/Companies/ActingCharacterRequestDto.cs \
        src/Companies/Companies.Api/CompanyEndpoints.cs \
        openapi/eliferpg-api-v1.json
git commit -m "Add company application HTTP endpoints"
```

---

## Task 10: Integration tests

**Files:**
- Modify: `tests/Companies.IntegrationTests/CompanyCommandTests.cs`

**Interfaces:**
- Consumes: every command/query from Tasks 6-8, plus existing `CreateCompanyCommand`, `AddMemberCommand`, `CompanyMemberPermissionsQuery`, and the existing private helpers `CreateCharacterAsync(IMediator)`/`CreateCompanyAsync(IMediator)` already defined in this file.
- Produces: nothing further consumed by other tasks — this is the final task.

This test class requires the local infra stack (`docker compose up -d`), per its existing class-level doc comment — same prerequisite as every other test in this file.

- [ ] **Step 1: Add the new test methods to `CompanyCommandTests.cs`**

Add these `[Fact]` methods before the closing brace of the `CompanyCommandTests` class (after `CompaniesQuery_IncludesCreatedCompany`, before the private helper methods):

```csharp
    [Fact]
    public async Task SubmitApplication_ForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var applicantId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Please let me join."));

        Assert.True(result is SubmitApplicationResult.Submitted, $"Expected Submitted, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForUnknownCompany_ReturnsCompanyNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var applicantId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(new CompanyId(Guid.NewGuid()), applicantId, "Hire me."));

        Assert.True(result is SubmitApplicationResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, new CharacterId(Guid.NewGuid()), "Hire me."));

        Assert.True(result is SubmitApplicationResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForExistingMember_ReturnsAlreadyMember()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var memberId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, memberId));

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, memberId, "Hire me."));

        Assert.True(result is SubmitApplicationResult.AlreadyMember, $"Expected AlreadyMember, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_WithOpenApplication_ReturnsDuplicateApplication()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var applicantId = await CreateCharacterAsync(mediator);
        await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "First try."));

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Second try."));

        Assert.True(result is SubmitApplicationResult.DuplicateApplication, $"Expected DuplicateApplication, got {result}");
    }

    [Fact]
    public async Task CompanyApplicationsQuery_ForManagerCharacter_ReturnsApplications()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));

        var result = await mediator.Send(new CompanyApplicationsQuery(createdCompany.CompanyId, founderId));

        Assert.True(result is CompanyApplicationsResult.Found, $"Expected Found, got {result}");
        if (result is CompanyApplicationsResult.Found found)
        {
            Assert.Contains(found.Applications, a => a.CharacterId == applicantId && a.Status == CompanyApplicationStatus.Pending);
        }
    }

    [Fact]
    public async Task CompanyApplicationsQuery_ForNonManagerCharacter_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, rookieId));

        var result = await mediator.Send(new CompanyApplicationsQuery(companyId, rookieId));

        Assert.True(result is CompanyApplicationsResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task ConfirmApplication_ByManager_SetsInProgress()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new ConfirmApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is ConfirmApplicationResult.Confirmed, $"Expected Confirmed, got {result}");
        var applications = await mediator.Send(new CompanyApplicationsQuery(createdCompany.CompanyId, founderId));
        if (applications is CompanyApplicationsResult.Found found)
        {
            Assert.Contains(found.Applications, a => a.Id == submittedApplication.ApplicationId && a.Status == CompanyApplicationStatus.InProgress);
        }
    }

    [Fact]
    public async Task AcceptApplication_ByManager_AddsMemberWithDefaultPermissions()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new AcceptApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is AcceptApplicationResult.Accepted, $"Expected Accepted, got {result}");
        var permissions = await mediator.Send(new CompanyMemberPermissionsQuery(createdCompany.CompanyId, applicantId));
        Assert.True(permissions is CompanyMemberPermissionsResult.Found, $"Expected Found, got {permissions}");
        if (permissions is CompanyMemberPermissionsResult.Found found)
        {
            Assert.Equal(CompanyPermissions.None, found.Permissions);
        }
    }

    [Fact]
    public async Task DenyApplication_ByManager_SetsDenied()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new DenyApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is DenyApplicationResult.Denied, $"Expected Denied, got {result}");
    }
```

- [ ] **Step 2: Build the test project to verify it compiles**

Run: `dotnet build tests/Companies.IntegrationTests/Companies.IntegrationTests.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Ensure the local infra stack is running**

Run: `docker compose up -d`
Expected: `postgres` and `keycloak` (and any other declared services) report healthy/running. If this environment cannot run Docker, skip to Step 5 and note that these tests could not be executed here.

- [ ] **Step 4: Run the new integration tests**

Run: `dotnet test tests/Companies.IntegrationTests/Companies.IntegrationTests.csproj --filter "FullyQualifiedName~Application"`
Expected: PASS, all 10 new tests green.

- [ ] **Step 5: Run the full Companies test suite (domain + integration) as a final sanity check**

Run: `dotnet test tests/Companies.Domain.UnitTests/Companies.Domain.UnitTests.csproj && dotnet test tests/Companies.IntegrationTests/Companies.IntegrationTests.csproj`
Expected: PASS, 0 failures across both projects.

- [ ] **Step 6: Commit**

```bash
git add tests/Companies.IntegrationTests/CompanyCommandTests.cs
git commit -m "Add integration tests for company applications"
```

---

## Final check

- [ ] Re-read `docs/superpowers/specs/2026-08-14-company-applications-design.md` end to end and confirm every section maps to a completed task above.
- [ ] Run `dotnet build ELifeRPG.Core.slnx` from the repo root and confirm the whole solution builds clean.
