# Account blocking & the login flow

## Context

The login flow (`player-connected` → list characters → `character-selected`) already works end-to-end, but the underlying `Account` aggregate's `Locked` status (event-sourced, with `Lock()`/`Unlock()` domain methods and `AccountLocked`/`AccountUnlocked` events) is currently a dead end: there is no HTTP endpoint to actually lock an account, and a locked account isn't communicated to the caller as data — `session-bootstrap` just fails with an opaque `403 "Account locked"` `Results.Problem`. This spec designs the missing half: how an operator blocks an account, and how the mod finds out and responds.

## Goals

- Let an operator/admin lock (block) and unlock an account over HTTP.
- Let `player-connected` report a blocked account as a normal, successful response the mod can branch on — not an error.
- Make sure a block actually takes effect for a player who is mid-session, without adding a status check to every write path in the system.

## Non-goals

- Renaming the domain concept from `Locked` to `Blocked` — the domain/application layer keeps `AccountStatus.Locked`/`Account.Lock()`/`Unlock()`; only the outward-facing DTO renders it as `"blocked"`.
- A distinct "Banned" status separate from "Locked" — one status covers this.
- Per-write-handler enforcement in `Characters`/`Banking`/`Companies` (a poll loop or scattered `AccountLocked` checks were both considered and dropped — see "Enforcement model" below).
- Real ArmA Reforger mod integration (kicking a connected player from the game process itself) — out of scope, same as the rest of the login flow's mod integration today.

## Enforcement model

The player-impersonating access token the Bridge exchanges for is a locally-validated JWT (`src/Api` checks signature/expiry against Keycloak's JWKS, no per-request introspection call). That means **no mechanism here can retroactively kill a token already in a player's hands** — enforcement is necessarily bounded by that token's remaining TTL. Given that, three options were considered:

1. **Per-write-handler checks** (add an `AccountLocked` result case to every mutating command in `Characters`/`Banking`/`Companies` that has an acting character). Rejected: doesn't tighten the enforcement window at all (the token still works for read-modeled auth either way), just spreads the same bounded guarantee across many call sites for no extra benefit.
2. **A Bridge-side poll loop** that periodically re-checks each connected player's status and flags them for a kick. Rejected in favor of (3) — push beats poll, and the actual "kick" action is unbuilt mod integration anyway.
3. **Short token TTL + an application-layer status gate at token-issuance time** (chosen): `player-connected` checks `session.Status` before ever attempting the Keycloak token-exchange call — a locked account never reaches that call, so it never gets a new token. The already-issued token, if any, dies on its own within the TTL window. This is a single choke point instead of many, and matches the same bounded-window guarantee the other two options would have given anyway.

**Token TTL:** set the player-impersonation token's lifespan to a short value (~5 minutes) via Keycloak client/realm config — not application code.

**Keycloak's `enabled` flag is not an independent backstop for this — verified empirically, not merely assumed.** The original version of this section claimed that `PUT /admin/realms/{realm}/users/{id}` `{enabled: false}` would make Keycloak itself refuse a subsequent impersonation-based token-exchange (`requested_subject=<username>`) for that user, independent of any application-level check. That's false: tested directly against a live Keycloak 26.0.8 instance, a disabled user's `requested_subject` exchange still succeeds and returns a valid, usable token — Keycloak enforces `enabled` for normal login grants (confirmed: password grant against the same disabled user correctly fails with `Account disabled`) but not for this specific delegated-exchange grant. See the "Post-implementation correction" section at the end of this document for the full investigation. `Lock()`/`Unlock()` still call `DisableUserAsync`/`EnableUserAsync` — disabling the user is still correct hygiene (blocks normal login, keeps the `enabled` state accurate) — but it is not what stops the Bridge from getting a new player token for a locked account. That's entirely the application-layer check in `player-connected`, described in the Bridge section below.

## Components

### Accounts module

- **`Accounts.Application`**
  - `CreateSessionCommand`'s result collapses from the two-case union (`Created`/`Locked`) to a single `CreateSessionResponse(AccountId, KeycloakUsername, AccountStatus Status)` record — no union needed once there's only one outcome shape. A locked account no longer short-circuits into an error; `Status` just reflects it.
  - New `LockAccountCommand`/`UnlockAccountCommand`: look up the account, call `Account.Lock()`/`Unlock()`, catch `AccountStatusException` for the already-locked/already-unlocked case. Union results: `Locked`/`AlreadyLocked`/`AccountNotFound` (and the `Unlocked`/`AlreadyUnlocked`/`AccountNotFound` mirror). On the locking path, also call the new Keycloak provisioner methods below; on unlocking, call the enable mirror.
  - `IKeycloakUserProvisioner` gains `DisableUserAsync(KeycloakUserId, CancellationToken)` and `EnableUserAsync(KeycloakUserId, CancellationToken)` — same shape as the existing `EnsureUserAsync`.
- **`Accounts.Infrastructure`**
  - `KeycloakUserProvisioner` implements the two new methods using the same raw-`HttpClient`-against-the-admin-API pattern `EnsureUserAsync` already uses (`PUT admin/realms/{realm}/users/{id}` with `{enabled: false}`/`{enabled: true}`), reusing the existing `account-service` client — its `manage-users` role already covers this, no new Keycloak client permissions needed.
- **`Accounts.Api`**
  - `SessionDto` gains a `Status` string field (`"active"`/`"blocked"`, computed from `AccountStatus`). `POST /api/accounts/session-bootstrap`'s endpoint mapping drops the `403 "Account locked"` branch — every successful lookup/create now returns `200` with `SessionDto`.
  - New endpoints: `POST /api/accounts/{accountId}/lock`, `POST /api/accounts/{accountId}/unlock`, each dispatching the corresponding command and mapping its union result: `404` for not-found, `200`/`204` for success. The already-locked/already-unlocked case is treated as an idempotent success (same status code as a fresh lock/unlock), not an error — mirrors the existing idempotent-by-design convention `Character.StartSession()` already uses for the analogous "already active" case.
  - New auth policy requiring a **new Keycloak scope** (e.g. `accounts:manage`), deliberately not folded into `gameserver-dev`'s scopes — banning a player is an admin/staff capability, not something the game server itself does. Add a new client (e.g. `staff-admin-dev`) to `infra/keycloak/eliferpg-realm.json` granted this scope, mirroring how `gameserver-dev` is set up today.

### Bridge

- `PlayerConnectedResponse` becomes `(Guid AccountId, string Status, string? PlayerAccessToken, int? ExpiresInSeconds)`.
- If `Status == "blocked"`: skip the Keycloak token-exchange call entirely (no token minted) and skip `PlayerSessionTracker.Start(...)` — a blocked player is never recorded as connected.
- If `Status == "active"`: unchanged behavior (token exchange + tracker start), just now reading `Status` off the response instead of inferring "not-403 means active."
- `character-selected` and `player-disconnected` gain the same `ProblemDetails`-catching pattern `player-connected` already has (currently missing — a `404`/other error from the Central API bubbles up as an unhandled Kiota exception today). This is an existing gap, not newly introduced, but it's directly in the code path this spec touches so it's fixed alongside it.

## Error handling

- `session-bootstrap` no longer produces an error response for a locked account — that's the whole point (Goal 2). It can still fail for unrelated reasons (missing/invalid Bridge token, wrong scope, unexpected 5xx) — those paths are untouched.
- `lock`/`unlock` endpoints: `404` for an unknown `accountId`; already-locked/already-unlocked is not an error (idempotent).
- Bridge's `character-selected`/`player-disconnected`: Central API errors now translate to a Bridge-side `Results.Problem` instead of an unhandled exception.

## Testing

- `Accounts.Domain.UnitTests`: no changes needed — `Lock()`/`Unlock()` invariants are already covered.
- `Accounts.IntegrationTests`: update the existing `CreateSessionCommand` test for the new single-shape response; add a test that locks an account (via `LockAccountCommand`) and verifies `session-bootstrap` afterward returns `Status: Locked` with no error; add a test verifying a locked account's Keycloak user is actually disabled — note this test does **not** also verify a subsequent token-exchange attempt fails, because it doesn't: see "Post-implementation correction" below; add the `Unlock` mirror.
- Manual verification (matching this repo's existing curl-walkthrough convention in `docs/accounts.md`/`docs/bridge.md`): lock an account, confirm `player-connected` returns `{status: "blocked"}` with no token, confirm a previously-issued token still works until it naturally expires (~5 min) and then a reconnect attempt is rejected the same way.

## Decisions log

- Status value exposed to callers: `"blocked"` (string), domain keeps `AccountStatus.Locked` internally — deliberate naming split, not an oversight.
- Lock/unlock authority: new admin/staff Keycloak scope, not the gameserver Bridge client.
- Blocked accounts get no player-access token at all, rather than a token with downstream enforcement.
- Enforcement is centralized at token-issuance time via an application-layer status check in `player-connected` (skips the Keycloak exchange call for a blocked account) plus the token's short TTL bounding any already-issued token — not spread across write handlers or a Bridge poll loop, and **not** via a Keycloak-level backstop on the exchange grant (that was the original plan; empirically disproven — see "Post-implementation correction" below). Both rejected alternatives (per-write-handler checks, a poll loop) were designed in full and then dropped once this approach was believed to cover the same guarantee more simply; that belief was accurate about the guarantee's bound (still true) but wrong about the mechanism providing it (corrected).

## Post-implementation correction (2026-08-14)

During final review, the spec-mandated test proving "a locked account's Keycloak user is actually disabled *and a subsequent token-exchange attempt against live Keycloak fails*" (see Testing, above) was added and failed: the exchange succeeded anyway. Investigation (raw curl against the live instance, independent of any test/app code) confirmed this is a genuine Keycloak behavior, not a bug in this branch's code — the classic impersonation-based token-exchange grant does not check the target user's `enabled` flag, unlike normal login grants. A follow-up research pass evaluated closing this at the Keycloak layer (fine-grained admin permissions on the impersonate action, a bindable auth flow for token-exchange, other config-only options) and found none apply to this grant path; the only real fix is a custom Keycloak `TokenExchangeProvider` SPI — a multi-day change (custom container image/build path, Java development against an internal Keycloak interface, realm-wide blast radius risk) — scoped as a separate future initiative, not part of this feature.

Decision: ship with the application-layer gate as the documented, sole enforcement boundary (see the corrected Enforcement model above). This is complete coverage for every path that exists in this codebase today — `BridgeTokenProvider.ExchangeForPlayerTokenAsync` is the only code that ever performs this exchange, and it only runs after `player-connected`'s status check — but is not defense-in-depth against a future bug in that check or a future caller that bypasses it. The spec-mandated test was inverted (documents the known Keycloak limitation instead of asserting the disproven guarantee) rather than deleted, so it keeps failing loudly if this analysis or Keycloak's behavior ever needs re-checking.

## Follow-up: closing the defense-in-depth gap (2026-08-14)

A second research pass went deeper on the custom `TokenExchangeProvider` SPI route floated above: Keycloak's provider-selection model turns out to support a *delegating* implementation (a new factory claims the same grant at higher priority via `supports()`/`order()`, rather than subclassing or reimplementing Keycloak's internal exchange class), which narrows the core implementation to roughly 1-2 days rather than an open-ended reimplementation. That changes the time estimate, but not the underlying cost that actually matters here: it still requires a permanent dependency on Keycloak's *private*, non-public SPI, a new JVM/Maven toolchain this repo has never had, and a custom Keycloak container image replacing the stock one — with no compatibility guarantee across Keycloak upgrades. Weighed against that, the decision is to **not** build the SPI.

Instead, the residual gap — "a future bug or future caller could invoke the token-exchange without first checking account status" — is closed structurally, in application code, with no Keycloak changes at all: fold the status check into `BridgeTokenProvider.ExchangeForPlayerTokenAsync` itself, so the method cannot hand back a token for a blocked account regardless of what any caller does or forgets to do beforehand. See `docs/superpowers/plans/2026-08-14-bridge-token-exchange-status-gate.md` for the implementation plan. The custom-SPI route remains documented here as a rejected alternative, not a deferred one — it is not expected to be picked up later unless the private-SPI risk tradeoff is revisited.
