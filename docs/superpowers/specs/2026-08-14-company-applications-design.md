# Company Applications — Design

## Summary

Add a "apply to a company" feature to the `Companies` module. A character
submits an application (a single free-text field, max 1000 characters) to a
company. Members of that company holding the `CompanyPermissions.ManageMembers`
flag can list applications, confirm an application (Pending → InProgress), and
accept or deny it. Accepting adds the applicant as a company member in the
company's default (lowest-ranked) position — the same position `AddMember`
already grants when no explicit position is given.

This is also the first place within the `Companies` module itself that
`CompanyPermissions.ManageMembers` is actually enforced; today it's modeled on
`CompanyPosition` but nothing checks it (see the doc comment on
`CompanyPermissions`).

## Domain (`Companies.Domain`)

### New types

- `CompanyApplicationId` — strongly-typed id (`[StronglyTypedId] public partial struct CompanyApplicationId;`), same shape as `CompanyPositionId`. Lives in `Companies.Domain` (module-local, not `Shared.Kernel`) since nothing outside `Companies` needs it.
- `CompanyApplicationStatus` — enum: `Pending`, `InProgress`, `Accepted`, `Denied`.
- `CompanyApplication` — `sealed record CompanyApplication(CompanyApplicationId Id, CharacterId CharacterId, string Message, CompanyApplicationStatus Status)`, same style as `CompanyMembership`.

### New exceptions (`Companies.Domain/Exceptions`)

- `DuplicateApplicationException` — thrown by `SubmitApplication` when the character already has an open (Pending/InProgress) application to this company. Reapplying after a Denied application is allowed.
- `ApplicationNotFoundException` — thrown by `ConfirmApplication`/`AcceptApplication`/`DenyApplication` when the `CompanyApplicationId` doesn't exist on this company.
- `InvalidApplicationStateException` — thrown when an action isn't valid for the application's current status (see transitions below).

### `Company` aggregate changes

New property: `List<CompanyApplication> Applications { get; private set; } = [];`

New methods (same "mutate + return event" style as `AddMember`):

```csharp
public ApplicationSubmitted SubmitApplication(CharacterId characterId, string message)
```
- Throws `AlreadyMemberException` (existing) if `characterId` is already a member.
- Throws `DuplicateApplicationException` if `characterId` has an existing `Pending` or `InProgress` application.
- No length/emptiness validation in the domain — enforced at the API boundary (see below); the domain trusts its callers here, consistent with `Company.Name` having no such guard either.

```csharp
public ApplicationConfirmed ConfirmApplication(CompanyApplicationId applicationId)
```
- Throws `ApplicationNotFoundException` if unknown.
- Throws `InvalidApplicationStateException` unless status is `Pending`. (Confirm is the one transition that stays strict — it only ever means Pending → InProgress.)

```csharp
public (ApplicationAccepted AcceptedEvent, MemberAdded MemberAddedEvent) AcceptApplication(CompanyApplicationId applicationId)
```
- Throws `ApplicationNotFoundException` if unknown.
- Throws `InvalidApplicationStateException` if status is already `Accepted` or `Denied`. Valid from `Pending` or `InProgress` (per the flexible-workflow decision).
- Internally calls the existing `AddMember(application.CharacterId)` (no explicit position → defaults to the lowest-ranked/highest-`Ordering` position, same as today). Propagates `AlreadyMemberException` in the (edge-case) scenario the character became a member through another path between applying and being accepted.

```csharp
public ApplicationDenied DenyApplication(CompanyApplicationId applicationId)
```
- Same not-found/state rules as Accept, valid from `Pending` or `InProgress`.

### New events (`Companies.Domain/Events`)

- `ApplicationSubmitted(CompanyId Id, CompanyApplicationId ApplicationId, CharacterId CharacterId, string Message)`
- `ApplicationConfirmed(CompanyId Id, CompanyApplicationId ApplicationId)`
- `ApplicationAccepted(CompanyId Id, CompanyApplicationId ApplicationId)`
- `ApplicationDenied(CompanyId Id, CompanyApplicationId ApplicationId)`

All four get `Apply(...)` overloads on `Company` (list add / `with`-expression status update, matching the immutable-record-in-a-list style `Memberships` already uses) and corresponding `Apply` overloads registered on `CompanyProjection`.

Events append to the company's existing single Marten stream — same pattern `CreateCompanyCommand` uses to append `CompanyCreated` + `MemberAdded` together. `AcceptApplication`'s two returned events (`ApplicationAccepted` + `MemberAdded`) get appended together and committed in one `SaveChangesAsync`, mirroring `CreateCompanyHandler`.

## Application layer (`Companies.Application/Companies`)

New commands/query, each following the existing `union`-result + handler convention:

- `SubmitApplicationCommand(CompanyId CompanyId, CharacterId CharacterId, string Message) : IRequest<SubmitApplicationResult>`
  `union SubmitApplicationResult(Submitted(CompanyApplicationId ApplicationId), CompanyNotFound, CharacterNotFound, AlreadyMember, DuplicateApplication)`
  Handler: load company (404 if missing) → `CharacterLookupQuery` (404 if unknown character, same as `AddMemberHandler`) → `company.SubmitApplication(...)`, catching `AlreadyMemberException`/`DuplicateApplicationException` → append + save.

- `CompanyApplicationsQuery(CompanyId CompanyId, CharacterId ActingCharacterId) : IRequest<CompanyApplicationsResult>`
  `union CompanyApplicationsResult(Found(IReadOnlyList<CompanyApplication> Applications), CompanyNotFound, NotAuthorized)`

- `ConfirmApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId) : IRequest<ConfirmApplicationResult>`
  `union ConfirmApplicationResult(Confirmed, CompanyNotFound, NotAuthorized, ApplicationNotFound, InvalidState)`

- `AcceptApplicationCommand(...) : IRequest<AcceptApplicationResult>`
  `union AcceptApplicationResult(Accepted, CompanyNotFound, NotAuthorized, ApplicationNotFound, InvalidState)`

- `DenyApplicationCommand(...) : IRequest<DenyApplicationResult>`
  `union DenyApplicationResult(Denied, CompanyNotFound, NotAuthorized, ApplicationNotFound, InvalidState)`

### Authorization helper

New internal `CompanyMemberAuthorization` (`Companies.Application/Common`), used by the query and all three management commands:

```csharp
internal static class CompanyMemberAuthorization
{
    public static bool CanManageMembers(Company company, CharacterId characterId)
    {
        var membership = company.Memberships.SingleOrDefault(x => x.CharacterId == characterId);
        if (membership is null) return false;
        var position = company.Positions.Single(x => x.Id == membership.PositionId);
        return position.Permissions.HasFlag(CompanyPermissions.ManageMembers);
    }
}
```

This checks in-process against the already-loaded `Company` aggregate rather than going back through `CompanyMemberPermissionsQuery` (that query exists for *other modules*, e.g. Banking, which can't see `Companies.Domain` internals directly — see `ARCHITECTURE.md` §9e — but `Companies.Application` already has the aggregate loaded, so a second round-trip would be redundant).

Handlers for Confirm/Accept/Deny/List all: load company (`CompanyNotFound` if missing) → `CanManageMembers` check (`NotAuthorized` if false) → invoke the domain method, catching `ApplicationNotFoundException` → `ApplicationNotFound`, `InvalidApplicationStateException` → `InvalidState`.

## API layer (`Companies.Api`)

New DTOs in `Companies.Api/Companies`:
- `SubmitApplicationRequestDto { Guid CharacterId; string Message; }` → `ToCommand(Guid companyId)`
- `CompanyApplicationDto { Guid ApplicationId; Guid CharacterId; string Message; string Status; }` → `Create(CompanyApplication source)` (status serialized via `ToString()` on the enum, matching how other enums/flags in this codebase are exposed — no separate DTO enum needed)
- `ActingCharacterRequestDto { Guid ActingCharacterId; }` — one shared DTO reused by confirm/accept/deny, with three `ToCommand` overloads (`ToConfirmCommand`, `ToAcceptCommand`, `ToDenyCommand`), each taking `(Guid companyId, Guid applicationId)` and returning the matching command type. One DTO shape, three named mapping methods — avoids three structurally-identical DTO classes.

New endpoints on `CompanyModule`, all under the existing `CompaniesWritePolicy` scope (same as `AddMember`/`CreateCompany` — this API has no per-user auth today, callers act on behalf of a `characterId`/`actingCharacterId` they pass explicitly, same as every other write endpoint in this module):

- `POST companies/{companyId:guid}/applications` — body `SubmitApplicationRequestDto`. Manually checks `Message.Length <= 1000` before sending the command, returning `Results.Problem(400)` on violation — matching the manual-validation style already used in `BankingEndpoints` (the exactly-one-of-characterId/companyId check), since this codebase has no attribute-based validation pipeline.
  Maps: `Submitted` → 200 w/ a `CompanyApplicationDto` built from the request's `CharacterId`/`Message` plus the result's new `ApplicationId` and status `Pending` (the handler only returns the id, not the full record, so the endpoint assembles the DTO the same way `CreateCompany`'s endpoint assembles `CompanyDto.Create(created, request.Name)` from a partial result), `CompanyNotFound`/`CharacterNotFound` → 404, `AlreadyMember` → 409, `DuplicateApplication` → 409.

- `GET companies/{companyId:guid}/applications?actingCharacterId={guid}` — query param since GET has no body (first endpoint in this module needing an acting-identity on a GET).
  Maps: `Found` → 200 w/ `List<CompanyApplicationDto>`, `CompanyNotFound` → 404, `NotAuthorized` → 403.

- `PUT companies/{companyId:guid}/applications/{applicationId:guid}/confirm` — body `ActingCharacterRequestDto`.
- `PUT companies/{companyId:guid}/applications/{applicationId:guid}/accept` — body `ActingCharacterRequestDto`.
- `PUT companies/{companyId:guid}/applications/{applicationId:guid}/deny` — body `ActingCharacterRequestDto`.

  All three map: success → 200, `CompanyNotFound`/`ApplicationNotFound` → 404, `NotAuthorized` → 403, `InvalidState` → 409.

**Explicitly not doing:** `CompanyDetailsDto` (`GET companies/{companyId}`) is *not* extended with applications — that endpoint only requires plain `.RequireAuthorization()` today with no `ManageMembers` gate, so exposing applicant messages there would bypass the permission check this whole feature exists to enforce. Applications are only reachable through the new gated endpoints.

## Testing

- `Companies.Domain.UnitTests`: new tests on `Company` for each of the four new methods — happy path, `AlreadyMemberException`/`DuplicateApplicationException` on submit, not-found and invalid-state exceptions on confirm/accept/deny, default-position assignment on accept, and an `Apply`-replay test (matching `Apply_ReplayingCreatedThenMemberAdded_ResultsInSameMembership`).
- `Companies.IntegrationTests`: end-to-end command/query round-trips through the mediator (matching `CompanyCommandTests`' existing style) covering: submit → list (authorized/unauthorized) → confirm → accept (member added with default permissions) and → deny, plus the not-found/already-member/duplicate-application/invalid-state result branches.

## Out of scope

- A "my applications" query for the applying character (not requested).
- Position selection on accept (always defaults, matching `AddMember`'s existing default behavior).
- Notifications/events consumed by other modules.
- Rate-limiting or throttling repeated applications.
