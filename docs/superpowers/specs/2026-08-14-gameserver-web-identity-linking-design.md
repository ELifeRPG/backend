# Disconnect token revocation & web/gameserver identity linking

## Context

The Bridge already does the token-exchange impersonation flow the way it was
originally proposed: it holds its own Keycloak client-credentials and
exchanges those for a token that genuinely impersonates the connecting player
(`sub` = the player's real Keycloak user id), via the `impersonation`
client-role mechanism (`ARCHITECTURE.md` §4.3,
`BridgeTokenProvider.ExchangeForPlayerTokenAsync`). That mechanism itself
needs no changes. This spec covers two gaps in what happens around it:

1. **Disconnect doesn't revoke anything.** `player-disconnected`
   (`src/Bridge/Bridge.Host/SessionLocalEndpoints.cs`) only clears
   Bridge-local bookkeeping (`PlayerSessionTracker`) and ends the active
   character session in the Central API. The player's access token keeps
   working against every proxied endpoint until it naturally expires (~5 min
   TTL, `accessTokenLifespan` in `infra/keycloak/eliferpg-realm.json`). The
   companion account-blocking spec
   (`2026-08-14-account-blocking-login-flow-design.md`) deliberately chose
   *not* to add per-request enforcement for the "account locked" case,
   reasoning the token TTL already bounds the window. Disconnect is a
   different trigger with a different cost/benefit: we want the token to stop
   working immediately, not just within the TTL window.
2. **No way to reconcile a player's gameserver identity (Bohemia ID →
   auto-provisioned, passwordless Keycloak user) with a future companion web
   portal identity (Discord/Steam login via Keycloak identity brokering).**
   `ARCHITECTURE.md` §8 already sketches one direction of this ("device code
   shown in-game, entered on the website") as a future consideration. This
   spec designs it fully, in both starting orders, including the case where
   both identities already independently exist with their own history before
   the player ever links them.

## Goals

- A disconnected player's access token stops being accepted by the Central
  API immediately, not just after its TTL elapses.
- A player can link their gameserver identity and a Discord/Steam-authenticated
  web identity together, starting from either side.
- A player who *already* has two independent identities (played on the
  gameserver, separately signed up on the portal) can still end up with one
  account.

## Non-goals

- Building the companion web portal itself. This spec covers the Central
  API / Keycloak / Bridge contract the portal would call — the portal is a
  future, separate consumer, the same way the Admin UI lives in its own repo
  (`ARCHITECTURE.md` §9b).
- Steam identity brokering mechanics. Steam uses OpenID 2.0, not OAuth2/OIDC,
  so it doesn't drop into Keycloak's generic broker the way Discord does. This
  spec designs the linking flow generically enough to support it later, but
  actually wiring Steam is flagged as a follow-up spike, not built here.
- Reconciling Characters/Banking/Companies data across two accounts during a
  merge. See "Enforcement model" under Merging — this is a deliberate scope
  cut enabled by a specific, load-bearing design choice below, not an
  oversight.

---

## Part 1 — Revoking the player token on disconnect

### Enforcement model

`src/Api` validates bearer tokens purely locally against Keycloak's JWKS
(signature + `exp`), with no per-request introspection call
(`src/Api/Program.cs`, the single `AddJwtBearer` call). A stateless JWT still
within its `exp` keeps validating no matter what happens Keycloak-side, so
disabling or logging out the Keycloak user — the mechanism the blocking spec
uses to stop *new* tokens from being minted — does not stop an
*already-issued* token from working. Killing a specific token on demand
requires a revocation check somewhere on the request path.

The blocking spec explicitly rejected adding this kind of check, but for a
different reason: it was weighing per-write-handler checks that wouldn't
tighten the enforcement window at all versus a single choke point at
token-issuance time, and issuance-time enforcement was sufficient for "can
this now-locked user get a new token." Disconnect isn't about stopping new
tokens — the same player may reconnect and get a legitimate new one seconds
later — it's about killing one specific, already-issued token the moment its
session ends. That's a genuinely different, narrower guarantee, and the
tradeoff (one extra store lookup per request) is worth it for that guarantee.

### Design: `jti` denylist, one choke point

- **`Accounts.Application`**: new `ITokenRevocationStore` —
  `void Revoke(string jti, DateTimeOffset expiresAt)` /
  `bool IsRevoked(string jti)`.
- **`Accounts.Infrastructure`**: in-memory implementation
  (`ConcurrentDictionary<string, DateTimeOffset>`, lazy eviction of entries
  past `expiresAt`). Same "in-memory, lost on restart" tradeoff
  `PlayerSessionTracker` already makes, and acceptable for the same reason —
  worst case on a Central API restart, a revoked-but-not-yet-expired token
  briefly works again, still bounded by its original ~5 min TTL.
- **`src/Api/Program.cs`**: the single `AddJwtBearer(...)` call gains
  `options.Events = new JwtBearerEvents { OnTokenValidated = ctx => {...} }` —
  read the `jti` claim off the validated principal, check
  `ITokenRevocationStore.IsRevoked`, `ctx.Fail(...)` if revoked. One hook,
  applies to every module's endpoints uniformly — the same "single choke
  point instead of many" reasoning the blocking spec already used, just
  applied at request-validation time instead of issuance time.
- **New endpoint**, `Accounts.Api`: `POST /api/accounts/tokens/revoke`
  `{ Jti: string, ExpiresAt: DateTimeOffset }` → `204`. Gated by a new scope,
  `gameserver:session:revoke` (mirrors the existing one-scope-per-capability
  convention: `gameserver:session:create`, `gameserver:characters:write`,
  etc., in `infra/keycloak/eliferpg-realm.json`), granted to `gameserver-dev`
  the same way the other gameserver scopes are.
- **Bridge (`src/Bridge/Bridge.Host`)**:
  - `PlayerSession` (`PlayerSessionTracker.cs`) gains `Jti` and `ExpiresAt`.
  - `player-connected` populates them by reading the claims off the token
    `BridgeTokenProvider.ExchangeForPlayerTokenAsync` just returned —
    `JwtSecurityTokenHandler().ReadJwtToken(token)`, no signature validation
    needed since Bridge just minted it itself.
  - `player-disconnected` reads `Jti`/`ExpiresAt` back off `sessions.End(...)`
    and calls the new revoke endpoint (via the existing Kiota-generated
    `EliferpgApiClient`, alongside the existing character-session-ending
    call).

### Verification needed before implementation

Matches this repo's existing "Verified against Keycloak 26.0.8" convention
(`ARCHITECTURE.md` §4.3): confirm a token minted via the `impersonation`-based
token-exchange grant actually carries a `jti` claim. Expected yes — Keycloak
stamps `jti` on token issuance regardless of grant type — but confirm against
the real dev Keycloak instance before wiring the rest of this phase, the same
way the original token-exchange mechanics were verified rather than assumed.

---

## Part 2 — Linking a gameserver identity and a web identity

### Keycloak configuration

- Register Discord as a realm Identity Provider in
  `infra/keycloak/eliferpg-realm.json` (`identityProviders`). Discord speaks
  standard OAuth2, so Keycloak's generic OAuth2/OIDC IdP config covers it
  without a custom broker.
- New public OAuth client for the portal — Authorization Code + PKCE, same
  shape as `account-console`. **Deliberately scoped to account-management
  claims only**: `profile`, `email`, and a new `account:self:manage` scope
  covering the linking endpoints below. **Never** any `gameserver:*` scope.
  This is the load-bearing decision that keeps Part 3 (merging) tractable —
  see below.
- `ARCHITECTURE.md` §4.4's trust-boundary list ("No component other than the
  Bridge ... may assert a player identity") gets a new bullet naming the
  portal's narrow exception: it may assert identity for account-management
  purposes only, never for gameplay actions.

### Domain changes (`Accounts` module)

- `Account.BohemiaId` becomes `GameId?` — a portal-first signup has no
  Bohemia ID yet. `AccountCreated` carries a nullable `BohemiaId`; one event
  shape covers both origins rather than branching into two event types, since
  both converge on the same `Account` aggregate immediately after creation.
- New `LinkingCode`: a short-lived (~10 min), single-use, server-generated
  code, stored as a **Marten document** (not in-memory like the revocation
  store — losing an in-flight code to an API restart mid-flow is a real bad
  user experience, unlike the revocation store's narrower blast radius).
  Maps `Code → initiating AccountId`.

### New endpoints (`Accounts.Api`)

- **`POST /api/accounts/links/codes`** — generates a code for the caller's
  own identity. Two distinct callers:
  - Bridge, on behalf of a connected player (`{BohemiaId}`, Bridge's own
    scope) — the "show a code in-game" direction.
  - The portal, using the player's own browser-obtained token (`sub` resolves
    the Account directly) — the "show a code on the website" direction.

  Same handler; the caller's identity determines which side is initiating.

- **`POST /api/accounts/links/redeem`** — the *other* side redeems the code:
  - Bridge calls this when a player types a web-shown code in-game
    (`{BohemiaId, Code}`).
  - The portal calls this when a player pastes an in-game-shown code on the
    site (`{Code}`, target resolved from the caller's own token).

  Redeem branches three ways:
  1. **Simple link** — one side has no counterpart yet (the gameserver
     Account has no federated identity yet, or the portal identity never
     became a standing Account with a `BohemiaId`). Attach the missing piece
     directly: Keycloak Admin API
     `POST /admin/realms/{realm}/users/{id}/federated-identity/{provider}` to
     attach Discord onto the gameserver-provisioned Keycloak user, or fill in
     `Account.BohemiaId` on the portal-provisioned Account. No second Account
     is ever created.
  2. **Already linked** — both sides already resolve to the same `AccountId`.
     Idempotent success — the same convention `Character.StartSession()` and
     the blocking spec's `Lock`/`Unlock` already use for "already in that
     state."
  3. **Merge** — both sides are already separate, independently-created
     Accounts → Part 3.

  Codes are single-use: redeeming (any branch) deletes the `LinkingCode`
  document.

---

## Part 3 — Merging two independently-created accounts

Only reachable via the redeem handler's third branch, and only tractable
because of Part 2's scoping decision: **the portal can never mint a token
capable of creating Characters, Bank Accounts, or Companies.** A
portal-originated Account can therefore never have accumulated gameplay data
before a merge happens — merging is an identity-only operation, not a
cross-module data migration.

- **Survivor policy:** the Bohemia-ID-bearing Account always wins — only it
  can carry gameplay data forward. The portal-only Account is retired.
- **`MergeAccountsCommand`** (`Accounts.Application`):
  1. Read the loser's federated identity via Keycloak Admin API
     (`GET /admin/realms/{realm}/users/{loserKeycloakUserId}/federated-identity`).
  2. Attach it to the survivor's Keycloak user (same attach call as Part 2's
     simple-link case).
  3. Delete the loser's Keycloak user record
     (`DELETE /admin/realms/{realm}/users/{loserKeycloakUserId}`) — it held
     nothing but the now-relocated federated identity link.
  4. Domain: new terminal `AccountStatus.Merged`, new
     `AccountMerged(AccountId LoserId, AccountId SurvivorId)` event,
     `Account.MergeInto(survivorId)` — same invariant-checking shape as
     `Lock()`/`Unlock()` (throws if already `Merged`).
  5. `AccountLookupQuery` and `CreateSessionCommand`'s result unions gain a
     `Merged(AccountId SurvivorId)` case, mirroring the existing `Locked`
     case, so a caller still holding the loser's `AccountId` gets redirected
     rather than silently failing.

**Explicitly not handled:** reconciling Characters/Banking/Companies data
across two Accounts. This is safe to skip *only* because of Part 2's scope
restriction on the portal client. If the portal is ever granted a gameplay
scope, this merge design needs revisiting before that ships — it would no
longer be safe to assume the loser Account is empty.

---

## Error handling

- `links/codes`: no error cases beyond standard auth failures — always
  succeeds for a valid caller identity (idempotent if a live code already
  exists for that identity: return the existing one rather than minting a
  second).
- `links/redeem`: `404`/`410`-equivalent `ProblemDetails` for an unknown or
  expired code; otherwise always succeeds into one of the three branches
  above — none of them are error states from the caller's point of view.
- `tokens/revoke`: `204` on success; no failure path that matters to the
  caller (Bridge fires-and-forgets this on disconnect — a failed revoke call
  degrades to the pre-existing TTL-bound guarantee, not to nothing).

## Testing

- **`Accounts.Domain.UnitTests`**: `Account.MergeInto` invariants (rejects
  merging an already-`Merged` account), mirroring the existing `Lock`/`Unlock`
  tests.
- **`Accounts.IntegrationTests`**:
  - Generate/redeem a linking code in both directions (simple-link case).
  - Redeem when already linked (idempotent success).
  - Redeem when both sides are independent accounts (merge path) — assert the
    loser's Keycloak user is gone and its federated identity now resolves on
    the survivor.
  - A revoked `jti` returns `401` on a subsequent request against a live
    Central API instance.
- **Manual walkthrough** (matches `docs/bridge.md`/`docs/accounts.md`'s
  existing curl convention): `player-connected` → confirm a proxied call
  succeeds → `player-disconnected` → replay the *exact same* bearer token
  directly against the Central API and confirm `401` immediately, not just
  "works until ~5 min pass."

## Decisions log

- Disconnect gets active revocation (a `jti` denylist checked once, centrally,
  in the JWT bearer pipeline), deliberately going further than the blocking
  spec's issuance-time-only enforcement — different trigger, different
  guarantee needed.
- Revocation store is in-memory (matches `PlayerSessionTracker`'s existing
  tradeoff); linking codes are a Marten document (different tradeoff,
  user-facing and worth surviving a restart).
- Web identity is Discord/Steam via Keycloak identity brokering; Steam's
  actual mechanics are deferred as a follow-up spike (OpenID 2.0, no native
  Keycloak broker).
- Linking supports both starting orders (gameserver-first, portal-first) via
  one `redeem` endpoint with three outcome branches, not two separate flows.
- The portal's OAuth client never gets a `gameserver:*` scope. This is what
  keeps account merging an identity-only operation — revisit the merge design
  if this restriction is ever relaxed.
- Merge survivor is always the Bohemia-ID-bearing account; the portal-only
  account is retired, not the other way around.
