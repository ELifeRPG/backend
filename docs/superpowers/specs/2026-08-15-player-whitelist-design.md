# Player whitelist

## Context

There is currently no way to restrict who can play on a tenant's gameserver(s). `session-bootstrap` provisions an `Account`/Keycloak user for any `BohemiaId` presented by a Bridge instance, and `player-connected` mints a player token for any account that isn't `Locked`. This spec adds an opt-in, per-server whitelist gate: a server can require accounts to have an approved application before they're allowed to receive a player token.

This design went through several shapes during brainstorming before settling here — notably, an earlier direction let staff pre-create whitelist entries by `DiscordId`/`BohemiaId` for players who didn't have an `Account` yet. That's dropped: applications are submitted by an existing `Account`, which resolves the tension for free (an account always exists by the time it applies) and keeps the feature to one mechanism instead of two.

## Goals

- A server can turn whitelist enforcement on/off independently of other servers on the same tenant.
- An account can submit one whitelist application per server, with free-form application text.
- Applications move through `Open → InReview → Approved`/`Rejected`, managed by staff holding a new tenant-wide Keycloak realm role.
- An account without an `Approved` application for a whitelist-enabled server gets no player token from `player-connected` — reported as ordinary data (a `Status` value), not an error, matching the existing `Locked`/`"blocked"` convention.
- An account that already has a player token, or that already has an `Account` but no application yet, is never blocked from `session-bootstrap` itself — only from receiving a *player* token.

## Non-goals

- Direct staff creation of a whitelist entry by `DiscordId`/`BohemiaId` for an account that doesn't exist yet — dropped, see Context.
- A player-facing way to check their own application status (e.g. `GET` by account) — deferred; staff can query the review queue, that's enough for v1.
- Editing application text after submission, or withdrawing an application — not requested; a rejected application can simply be re-submitted.
- Real Discord/Steam OAuth account linking (ARCHITECTURE.md §1.2/§8's non-goal) — unrelated; this feature never touches `DiscordId`.
- A separate registration step for a server distinct from what already exists — `GameServer` records are keyed by the existing Bridge `client_id` and upserted lazily (first `PATCH` creates it); there's no "create a server" ceremony beyond that. See "Server identity" below.
- A generic/dictionary-shaped settings bag — new settings are added as new typed fields on `GameServer`/`UpdateGameServerSettingsCommand`, not an untyped key-value store.

## Server identity

A gameserver has no representation in the Central API's own data today — it's only a Keycloak Client Credentials client (e.g. `gameserver-dev`). Rather than introduce a new "register a server" step, the per-server whitelist toggle keys directly off the calling client's `client_id`, which Keycloak already asserts on every Bridge-issued access token. `session-bootstrap`'s handler needs to read that claim to know which server a connecting player belongs to.

**Open item, to verify empirically before/at implementation start** (in the spirit of this repo's existing practice of confirming Keycloak behavior against a live instance rather than assuming — see the account-blocking spec's "Post-implementation correction"): decode a live `gameserver-dev` Client Credentials token and confirm the exact claim that carries its own client id (`azp` is the standard OIDC claim for this, but Keycloak's shape should be checked directly, the same way that spec found `enabled` didn't behave as assumed).

## Domain model

### `GameServer` (new — plain Marten document, not event-sourced)

Config, not domain history worth auditing — a single mutable document is a deliberate departure from this codebase's usual event-sourced-everything pattern. Modeled as a general per-server **settings resource** rather than a single-purpose whitelist toggle, since more server-level settings are expected soon; `WhitelistEnabled` is just the first field on it, not the shape of the resource:

```
GameServer
  ClientId: string          // Marten document id — the Bridge's own Keycloak client_id
  WhitelistEnabled: bool
```

No record for a given `ClientId` means every setting is at its default (`WhitelistEnabled: false` — default-safe: a server does nothing differently until staff explicitly opts in). The repository port (`IGameServerRepository.GetOrDefaultAsync`, below) returns a default-valued `GameServer` rather than `null` in this case, so callers never null-check.

### `WhitelistApplication` (new — event-sourced aggregate, same Marten pattern as `Account`)

```
WhitelistApplication
  Id: WhitelistApplicationId          // new StronglyTypedId
  AccountId: AccountId                // existing Shared.Kernel type
  ServerClientId: string
  ApplicationText: string             // free text, capped (e.g. 4000 chars) at the DTO layer
  Status: WhitelistApplicationStatus  // Open | InReview | Approved | Rejected
```

Events: `WhitelistApplicationSubmitted(Id, AccountId, ServerClientId, ApplicationText)`, `WhitelistApplicationReviewStarted(Id)`, `WhitelistApplicationApproved(Id)`, `WhitelistApplicationRejected(Id)`.

State machine:

```
Open --StartReview--> InReview --Approve--> Approved
                       InReview --Reject---> Rejected
```

`Approve`/`Reject` are only valid from `InReview` — staff must claim an application (`StartReview`) before deciding it. This is a default I picked (prevents two reviewers double-processing the same application, and gives a UI something to show as "someone's looking at this"); relax to allow deciding straight from `Open` if that turns out to be unwanted ceremony.

Idempotency, mirroring `Account.Lock()`/`LockAccountHandler`'s existing pattern (domain method throws on a genuine re-transition; the *handler* checks current status first and treats "already in the target terminal state" as a no-op success, not an error):

- `StartReview` on an already-`InReview` application: idempotent success.
- `Approve`/`Reject` on an application already in that exact terminal state: idempotent success.
- Any other mismatched transition (e.g. `Approve` on `Open`, or `Approve` on `Rejected`): `InvalidState`.

Invariant enforced by the **application layer**, not the domain type (matches `CreateSessionCommand`'s existing style — decided before events are constructed): only one `Open`/`InReview` application may exist per `(AccountId, ServerClientId)` at a time. Submitting again while one is pending returns `AlreadyPending`; submitting after a `Rejected` (or `Approved`) outcome is allowed and starts a fresh application.

`WhitelistApplicationStatusException` mirrors `AccountStatusException` for the domain-layer guard.

## Application layer (`Accounts.Application/Whitelist/`)

Ports:

```csharp
public interface IWhitelistApplicationRepository
{
    ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken ct);
    ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, string serverClientId, CancellationToken ct); // Open or InReview
    ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, string serverClientId, CancellationToken ct);
    ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken ct);
    void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted @event);
    void Append<TEvent>(WhitelistApplicationId id, TEvent @event) where TEvent : notnull;
    ValueTask SaveChangesAsync(CancellationToken ct);
}

public interface IGameServerRepository
{
    ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken ct); // never null — a missing record is a default-valued GameServer
    ValueTask UpsertAsync(GameServer server, CancellationToken ct);
}
```

Commands (all following the existing `public union Result(...)` + `IRequestHandler` shape):

- `SubmitWhitelistApplicationCommand(AccountId, string ServerClientId, string ApplicationText)` → `Submitted(WhitelistApplicationId) | AccountNotFound | AlreadyPending`. Looks up the account via the existing `IAccountRepository` (no new dependency) to produce `AccountNotFound`.
- `StartWhitelistApplicationReviewCommand(WhitelistApplicationId)` → `Started | NotFound | InvalidState`.
- `ApproveWhitelistApplicationCommand(WhitelistApplicationId)` → `Approved | NotFound | InvalidState`.
- `RejectWhitelistApplicationCommand(WhitelistApplicationId)` → `Rejected | NotFound | InvalidState`.
- `UpdateGameServerSettingsCommand(string ClientId, bool? WhitelistEnabled)` → no union needed, always succeeds (loads-or-defaults, applies whichever fields are non-null, upserts). Each new per-server setting adds one more nullable parameter here and one more field on `GameServer` — not a new command or endpoint.
- `WhitelistApplicationsQuery(WhitelistApplicationStatus Status)` → `IReadOnlyList<WhitelistApplicationSummary>` for the review queue (`WhitelistApplicationSummary` carries `Id`, `AccountId`, `ServerClientId`, `ApplicationText`, `Status`).

## Gate integration

`CreateSessionCommand` gains a second parameter: `CreateSessionCommand(GameId BohemiaId, string ServerClientId)`. The endpoint resolves `ServerClientId` from the caller's own bearer token (see "Server identity") and passes it in — the command itself stays a pure application-layer construct, no `HttpContext` reaching into the handler.

`CreateSessionResponse` changes from carrying the domain `AccountStatus` directly to a new application-layer `SessionStatus { Active, Blocked, NotWhitelisted }`, computed in the handler:

1. Look up/create the `Account` exactly as today (never gated — an account always gets created/found).
2. If `Account.Status == Locked` → `SessionStatus.Blocked` (unchanged behavior, just renamed onto the new enum).
3. Else, if `IGameServerRepository.GetOrDefaultAsync(ServerClientId).WhitelistEnabled` is true and no `Approved` `WhitelistApplication` exists for `(account.Id, ServerClientId)` → `SessionStatus.NotWhitelisted`.
4. Else → `SessionStatus.Active`.

`SessionDto.Status` gains the third string value: `"active" | "blocked" | "not_whitelisted"`.

**This section originally targeted the pre-refactor gate; `master` has since merged the planned refactor, so this now targets the current code.** The status gate no longer lives in `SessionLocalEndpoints.cs`'s control flow — it's enforced inside `BridgeTokenProvider.ExchangeForPlayerTokenAsync(string keycloakUsername, string status, CancellationToken)`, which today reads:

```csharp
if (status == "blocked")
{
    return null;
}
```

This generalizes to `if (status != "active") { return null; }` — a one-line change in `BridgeTokenProvider.cs`, not `SessionLocalEndpoints.cs`. The caller (`player-connected`) already branches on `ExchangeForPlayerTokenAsync` returning `null` vs. a token, so no change needed there. No `Bridge.ApiClient` regeneration needed either (`SessionDto`'s shape — an existing `string Status` field — doesn't change, only the set of values it can hold).

**This file now lives in a different repo.** `BridgeTokenProvider.cs`/`SessionLocalEndpoints.cs` are at `eliferpg-reforger-bridge`'s `src/Bridge.Host/` (see the note under API surface) — this one-line change is made and committed there, not in `eliferpg-core`.

## API surface

**Submission — proxied through the Bridge**, same shape as every other player-driven write in this codebase (Characters/Banking/Companies: the Bridge calls the Central API with an explicit `accountId`, never a direct player-JWT call — confirmed against `CharacterEndpoints.cs`). Mirrors `character-selected`'s pattern of resolving `AccountId` from the Bridge's local `PlayerSessionTracker` (populated by `player-connected`), so submission requires the player to be currently connected.

**Note:** Bridge now lives in its own repo, `eliferpg-reforger-bridge` (split out from `eliferpg-core` — see `master`'s `eea4b0c "Split Bridge Service out into its own repo"`, which landed after this spec's first draft). The Central API endpoint below is implemented in this repo; the `Bridge.Host` proxy handler is implemented in `eliferpg-reforger-bridge`'s `src/Bridge.Host/`, a separate change against that repo's own `master`.

```
Bridge.Host (eliferpg-reforger-bridge): POST submit-whitelist-application { bohemiaId, applicationText }
  -> looks up AccountId via PlayerSessionTracker, proxies to:
Central API (this repo): POST api/whitelist-applications { accountId, serverClientId, applicationText }
  [gameserver:whitelist:write]  -- serverClientId resolved from the Bridge's own token, same as session-bootstrap
  -> 200 { whitelistApplicationId, status: "open" } | 404 (account not found) | 409 (already pending)
```

**Review/admin — called directly against the Central API**, same as `lock`/`unlock` today (staff tooling hits `localhost:5100` directly, not through a Bridge):

```
POST api/whitelist-applications/{id}/start-review   [whitelist-reviewer role] -> 204 | 404
POST api/whitelist-applications/{id}/approve         [whitelist-reviewer role] -> 204 | 404 | 409 (invalid state)
POST api/whitelist-applications/{id}/reject          [whitelist-reviewer role] -> 204 | 404 | 409 (invalid state)
GET  api/whitelist-applications?status=open          [whitelist-reviewer role] -> 200 [ { id, accountId, serverClientId, applicationText, status }, ... ]
GET  api/game-servers/{clientId}                      [whitelist-reviewer role] -> 200 { clientId, whitelistEnabled } (defaults if never configured)
PATCH api/game-servers/{clientId}                     [whitelist-reviewer role] { whitelistEnabled? } -> 204 (partial update — omitted fields are left unchanged; future settings add fields here, not new endpoints)
```

## Authorization

Every existing staff/admin policy in this codebase (`accounts:manage`) is a client **scope** check (`context.User.FindFirst("scope")`). This feature introduces the first **role**-based policy instead — `whitelist-reviewer`, checked via the token's `realm_access.roles` claim — since a review action should be attributable to tenant-wide staff authority, not a specific client's granted scopes. Concretely:

- Add a `whitelist-reviewer` realm role to `infra/keycloak/eliferpg-realm.json`.
- Grant it to the existing `staff-admin-dev` service account (reusing the client already used for `accounts:manage`, rather than minting a new one) via a realm role mapping — distinct from how `accounts:manage` is granted today (a default client scope), since realm roles are assigned as role mappings, not client scopes.
- New policy: `RequireAssertion(context => context.User.FindFirst("realm_access")...)` — the exact claim shape (Keycloak nests realm roles inside a JSON object claim, not a flat space-delimited string like `scope`) needs confirming against a live token during implementation; ASP.NET's `services.AddAuthorizationBuilder().AddPolicy(..., policy => policy.RequireRole(...))` may already handle this once the JWT bearer handler's `RoleClaimType` is configured to unpack it — needs a spike, flagged for the implementation plan rather than assumed here.
- Submission keeps the existing scope-based model: new `gameserver:whitelist:write` client scope, added to `gameserver-dev`'s default scopes (parallel to `gameserver:characters:write` etc.).

## Infrastructure

- `WhitelistApplicationProjection : SingleStreamProjection<WhitelistApplication, WhitelistApplicationId>` in `Accounts.Infrastructure`, delegating to the aggregate's own `Create`/`Apply` methods — same convention `AccountProjection` already establishes (MIGRATION.md's documented gotcha: convention methods must live on the Infrastructure-side projection, not the Domain type, since Marten's source generator only runs in a project referencing `Marten`).
- `MartenWhitelistApplicationRepository` in `Accounts.Infrastructure/Common/`, same shape as `MartenAccountRepository`.
- `MartenGameServerRepository` — a plain `IDocumentSession.LoadAsync<GameServer>`/`.Store(...)` pair (with the load-or-default logic `GetOrDefaultAsync` promises), no event stream.
- Both new document/stream types register in `AddAccountInfrastructure` (`ServiceCollectionExtensions.cs`) alongside the existing `AccountProjection` registration — same Marten store, same `"account"` schema (this is a sub-concern of the `Accounts` module, not a new module, per the earlier "fold into Accounts" decision).
- `WhitelistApplicationId` — new `[StronglyTypedId]` (default `Guid` backing), placed in `Accounts.Domain` alongside `AccountId`'s siblings — no `Shared.Kernel` placement needed since nothing outside `Accounts` references it.

## Error handling

- `session-bootstrap`: unchanged 200-always contract; `NotWhitelisted` is reported as data (`Status`), never an error — matches `Locked`'s existing precedent exactly.
- `submit-whitelist-application`: `404` if the account somehow doesn't exist (shouldn't happen in practice — the Bridge only knows an `AccountId` after `player-connected` succeeded, which requires the account to exist), `409` for `AlreadyPending`.
- `start-review`/`approve`/`reject`: `404` for an unknown application id, `409` for a genuinely invalid transition (not the idempotent-retry case, which is a plain success).

## Testing

- `Accounts.Domain.UnitTests`: `WhitelistApplication`'s state machine — valid transitions, idempotent retries, invalid transitions throwing `WhitelistApplicationStatusException`.
- `Accounts.IntegrationTests`: submit → start-review → approve, verify a subsequent `session-bootstrap` for that account against that `ServerClientId` returns `"active"`; submit with no decision yet, verify `session-bootstrap` returns `"not_whitelisted"` when the server has whitelist enabled; verify `session-bootstrap` returns `"active"` regardless of application state when the server does *not* have whitelist enabled (the default/off case); duplicate-submission-while-pending returns `AlreadyPending`; a `Locked` account still reports `"blocked"` even with an `Approved` application (lock takes precedence).
- A spike/small integration test decoding a real `gameserver-dev`-issued token to confirm the claim name for the caller's own `client_id`, and a real `staff-admin-dev` (or newly-provisioned) token to confirm the realm-role claim shape — both are "verify against live Keycloak" items flagged above, not assumptions to carry into the implementation plan.
- Manual verification (matching `docs/accounts.md`/`docs/bridge.md`'s existing curl-walkthrough convention): toggle a server's whitelist on, confirm an unapproved account gets no token from `player-connected`, submit + approve an application, confirm the same account now gets a token.

## Decisions log

- Gate point: `player-connected`'s token-issuance check (via `session-bootstrap`'s `Status` field), not `session-bootstrap` itself blocking account creation — required once applications reference an existing `AccountId`.
- Dropped the earlier staff-direct-entry-by-`DiscordId`/`BohemiaId` design entirely in favor of the single application-based mechanism.
- Server identity: the existing Bridge `client_id`, no new `GameServer` registration entity.
- Application scope: per-server, not global to the account — a server can have its own applicant pool even though the same staff role reviews all servers.
- Whitelist review authority: a new Keycloak **realm role** (`whitelist-reviewer`), not a client scope — a deliberate first departure from this codebase's scope-only authorization model, per explicit instruction ("a global role").
- `GameServer` is a plain Marten document, not an event-sourced aggregate — server config isn't domain history worth auditing.
- Generalized the per-server config from a single whitelist toggle into a `GameServer` settings resource (`GET`/`PATCH api/game-servers/{clientId}`) up front, since more per-server settings are expected soon — avoids a new single-purpose endpoint per setting. Still typed fields on `UpdateGameServerSettingsCommand`, not an untyped dictionary — see Non-goals.
- `Approve`/`Reject` require `InReview` first (no direct `Open → Approved`/`Rejected`) — staff must claim an application before deciding it.
- Gate integration targets `BridgeTokenProvider.ExchangeForPlayerTokenAsync`'s status check (landed on `master` after this spec's first draft), not the originally-described `SessionLocalEndpoints.cs` control-flow check.
