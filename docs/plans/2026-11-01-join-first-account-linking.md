# Join-first linking, and one account per many game identities

## Status — 2026-11-01

**The backend is done and committed.** Branch `feat/join-first-and-multi-game-account-linking`
in the worktree at `.claude/worktrees/agent-a865b6a65a5c7611b`, two commits, not pushed.

| Step | State |
|---|---|
| 1. Rename | done |
| 2. Domain | done |
| 3. Infrastructure | done (no backfill — environments are pruned) |
| 4. Reconciler | done |
| 5. Optional hardening | **skipped** — see omissions below |
| 6. Docs | done (`docs/accounts.md`, `ARCHITECTURE.md` §4.3, `README.md` rollout note) |
| 7. SPI | **not started** — separate repo |
| 8. Join-first creation + `linkUrl` + `platform` | done |
| 9. Client → bridge → portal → mod | **not started** — separate repos |

Verified: 832 tests pass across all 16 projects against real Postgres and Keycloak, including a
4-way concurrent-join race against the unique index and every reconciler refusal.
`openapi/eliferpg-api-v1.json` is regenerated and committed.

**Deliberate omissions, all material:**

- **Step 5 (optional hardening) skipped**: no service-account token cache, and
  `KeycloakUserProvisioner.cs`'s dead `KeycloakUserRepresentation` is still there. Neither is
  needed for acceptance, and the fast path added in step 8 already means a steady-state join makes
  **zero** Keycloak calls, which was most of what the cache was for.
- **`GameAccountUnlinked` not added.** The plan listed it, but nothing produces it — no flow
  unlinks an identity. An event with no producer is speculative; add it with the feature that needs
  it.
- **No server-side log of reconciliation conflicts.** This codebase has no logging convention at
  all (there is an explicit comment declining to introduce one in `Shops.Api/ShopEndpoints.cs`), so
  the observable surface is the `409` from `POST /api/accounts/me`. A conflict during
  `session-bootstrap` is invisible server-side; the player still plays and still sees the link
  prompt.
- **One commit, not two.** The plan wanted the rename isolated for diff readability. The model was
  built on top of it in the same tree, and splitting retroactively risked a non-building
  intermediate commit for no real gain.

**One behaviour change the plan did not specify**, found by the race test: a join that loses the
creation race now re-resolves onto the winner's account instead of returning `unlinked`. The plan
only required "one account survives"; making the loser retry would have been a worse player
experience for no benefit.

## Context

Two problems, one change, because they touch the same surfaces.

**Identity is portal-first today**: a join by an unlinked game identity creates **nothing**.
`CreateSessionHandler` returns `status: "unlinked"` plus an 8-character PIN, and the player cannot
play until they sign up on the web portal and retype that PIN into Keycloak's
`link-bohemia-gameaccount` required action. Someone who just wants to join a server must go and
make a web account first.

**And identity is modelled 1:1, which is wrong.** `Account.BohemiaId` is a single `GameId?`, but
Arma Reforger's own model is 1:n: *"A Game Identity is created for each game-platform combination
on first network connection, so one player on several platforms can have several. A Bohemia
Interactive account is a single account per player, and a Game Identity can be linked to it."*
([wiki](https://community.bistudio.com/wiki/Arma_Reforger:Game_Identity)) A player on PC, Xbox and
PS5 holds three Game Identities and must be able to link all three to one web account. The field
name is itself part of the confusion: `BohemiaId` names the *web* identity while holding a
*per-platform game* identity, so it is renamed throughout (see **The rename**).

After this change all of the following work, with no web account required beforehand:

1. **Portal first** — account on Keycloak, then joins. Works today; must keep working.
2. **Join first** — joins with no account anywhere, plays immediately, links later.
3. **Many platforms** — each additional Game Identity links to the same account.

Linking becomes a copy-to-clipboard URL shown in game instead of eight characters retyped in a
browser. The URL points at Keycloak, which owns the whole browser flow — no portal involvement.

The two concerns are folded together rather than sequenced because 1:n touches every surface
join-first touches — the aggregate, both finders, the uniqueness guarantee, the Keycloak attribute
and claim, the reconciler and the SPI bind. Doing them in series would mean building each twice and
cutting two breaking api-client majors instead of one.

### Decisions taken

- A join by an unknown game identity **creates the account**, so the player plays at once.
- **Game identities are their own document, not a field on `Account`.** `Account` loses
  `BohemiaId` entirely and gains nothing: it becomes the web identity plus moderation state, and
  game identities are links pointing *at* it. Nothing on the aggregate needs the collection —
  status needs only "does any link exist", and staff and portal views are queries.
- **Uniqueness is on the identity alone, not on `(platform, identity)`.** Each platform
  combination gets its own Game Identity ID, so the ids are already globally unique and platform is
  *descriptive* metadata. That also means **many links per platform** are allowed; the only
  invariant is that one game identity belongs to at most one account.
- **`bohemiaId` → `gameIdentityId` everywhere, wire included.** Accepted as a breaking change; it
  is the only breaking part, since 1:n itself is additive on the wire.
- **All environments will be pruned, so no data is preserved.** That removes every migration
  concern this change would otherwise have carried: no Postgres backfill, no Keycloak attribute
  copy, no stored-JSON compatibility pins, no event-type aliases, and no expand/migrate/contract
  sequencing. Persisted names are free to follow the C# ones, which is why the rename is a plain
  rename. Volumes are recreated on rollout.
- `Account.KeycloakUserId` becomes **nullable** — a join-first account has no web identity until
  the player links. No provisional `bohemia_<uid>` Keycloak users: nothing consumes
  `keycloakUserId` (the live bridge dropped the player-token exchange; `player:self` exists only in
  prose), and a shell user would force a subject rebind the SPI cannot express.
- With hive whitelisting on, a brand-new join-first account reports **`not_whitelisted`** —
  whitelisting must not be bypassable by joining before signing up.
- The PIN stays the payload: already a single-use, server-held handle in Keycloak's
  `SingleUseObjectProvider` (atomic `remove()`, 30-minute TTL, all failure modes collapsed to
  `null` so there is no probing oracle). Stronger than a backend-signed JWT, with no signing key,
  no replay store, and no game identity in a pasted URL.
- **Keycloak's own login flow serves the browser flow — we add no browser endpoints.** The in-game
  URL is an ordinary authorization request carrying `kc_action` and the PIN, and the *existing*
  `LinkBohemiaGameAccountRequiredAction` does the binding. No portal route, no backend browser
  endpoints, and no new routes on the realm resource.

  This supersedes an earlier decision in this plan to add `GET`/`POST /link` to
  `BohemiaGameAccountResource`. That design existed only because reading a PIN off an authorization
  request was believed to be an unknown. **It is not** — verified against
  `quay.io/keycloak/keycloak:26.0.8`:
  `AuthorizationEndpoint.LOGIN_SESSION_NOTE_ADDITIONAL_REQ_PARAMS_PREFIX` is
  `"client_request_param_"`, `AuthzEndpointRequestParser` extracts up to
  `ADDITIONAL_REQ_PARAMS_MAX_MUMBER = 5` unknown params of `ADDITIONAL_REQ_PARAMS_MAX_SIZE = 2000`
  chars total, `link_pin` is not in `KNOWN_REQ_PARAMS` (so it is not swallowed), and
  `RequiredActionContext.getAuthenticationSession()` exposes the notes. Keeping the custom
  endpoints would have cost ~200 lines of browser code, two templates, and three unverified
  Keycloak-internals questions — all of which this deletes.
- **`LinkBohemiaGameAccountRequiredAction` gains one thing**: it reads the PIN from the auth
  session when the flow supplied one, and falls back to the typed form when it did not. The typed
  path therefore stays intact for anyone who cannot use the link, and there is only ever one
  implementation of consume-and-bind — the divergence risk the old design had to mitigate with a
  shared mapping simply does not arise.

## Flow

```
join, unknown game identity
  → Account created (KeycloakUserId null) + GameAccountLink created, pointing at it
  → status active (or not_whitelisted / blocked)
  → linkPin + linkUrl returned for the mod to show

join on a second platform, already-linked player
  → no link yet for that identity → Keycloak asserts it belongs to subject U
  → GameAccountLink created pointing at U's existing account — no second account

player opens linkUrl
  {keycloak}/realms/eliferpg/protocol/openid-connect/auth
    ?client_id=eliferpg-game-link&response_type=code&scope=openid
    &redirect_uri=/realms/eliferpg/bohemia-gameaccount/done
    &kc_action=link-bohemia-gameaccount&link_pin=UVQQM8ZU
  → Keycloak stores link_pin as auth-session note client_request_param_link_pin
  → not signed in? → Keycloak's own login page, which offers registration
  → signed in     → kc_action runs LinkBohemiaGameAccountRequiredAction
  → the action reads the note → confirmation form, prefilled ("link <bohemiaId> to <username>?")
  → player confirms (POST, Keycloak's own form token) → consumePin → bind
  → redirect to redirect_uri → "done" page (the code is never exchanged)

backend notices from either side
  → /accounts/me: game_identity_ids claim present → attach this subject to that account
  → session-bootstrap: Keycloak reports a binding for an account with no subject → attach
```

The binding lands in Keycloak only; the backend learns it on the player's next join or next portal
page load, which is why both claim paths below are load-bearing.

## Hazards that shape the design

These are why the plan is bigger than "add a create branch".

1. **Duplicate-key race, solved structurally by the 1:n model.** Join-first creation would let
   two `player-connected` calls for the same player (two servers in the hive, or a fast reconnect)
   both read "no account" and both create one — Marten raises no conflict on two `StartStream`s
   with different `AccountId`s, and from then on *every* join for that player 500s on
   `SingleOrDefaultAsync`.

   Moving game identities off the aggregate removes the problem rather than mitigating it. The
   link is its own document with the identity as a **required** natural key, so the second
   concurrent insert violates a unique index and the whole transaction rolls back — exactly the
   loud write-time failure we want, and it leaves one account. The pattern is already in this
   module: `GameServer` is surrogate-keyed with a string natural key under
   `UniqueIndex(x => x.ClientId)` (`Accounts.Infrastructure/ServiceCollectionExtensions.cs:24`),
   which is precisely why `MartenGameServerRepository.FindByClientIdAsync:11-12` can safely use
   `SingleOrDefaultAsync`. Copy it:

   ```csharp
   options.Schema.For<GameAccountLink>().Identity(x => x.Id).UniqueIndex(x => x.GameIdentityId);
   ```

   Use `session.Insert()` for the link, not `Store()`, so a duplicate throws instead of upserting.
   This supersedes an earlier version of this hazard that called for `ToListAsync` plus a
   deterministic pick and a three-mechanism index ladder; none of that is needed, and the
   uncertainty it hedged against — whether Marten can unique-index a *nullable strongly-typed id* —
   no longer arises, because the key is a plain required field on a document. **No unique index on
   `KeycloakUserId`**: the absorb transiently has two rows holding one subject inside a single
   transaction.

   One piece of the old hazard survives: a **null guard** on `FindByKeycloakUserIdAsync`, which is
   `x.KeycloakUserId.Value == keycloakUserId.Value` with no `!= null`. The comment at
   `MartenAccountRepository.cs:18-20` spells out what that costs once the id is nullable: every
   account without one matches whichever subject is asked for. That is worse than staleness —
   join-first accounts *and* absorbed losers (whose `KeycloakUserId` is deliberately nulled) would
   all be candidate matches, so `/accounts/me` would hand back the wrong account.
2. **A missing projection `Apply` is a duplicate-account generator, not cosmetic staleness.**
   `AccountProjection` is inline, so the document *is* what queries read. Without
   `Apply(KeycloakUserAttached)`, `FindByKeycloakUserIdAsync` never finds the account, so
   `/accounts/me` creates a fresh one on every call. The existing replay test
   (`AccountTests.cs:151`) cannot catch this — it calls `Apply` directly and bypasses the
   projection. Every new event needs a unit replay test **and** an integration test that appends
   through the repository, saves, and re-reads through a finder.
3. **Nothing maps `AccountStatusException` to a problem document**, and `session-bootstrap` is
   documented as always returning 200. Today the one throw on this path (`BindBohemiaId`'s
   same-id rebind, `Account.cs:38-41`) is unreachable; after this change a stale read or
   concurrent joins would reach it. The 1:n model removes that particular throw —
   `BindBohemiaId` is deleted, and re-linking an identity that already points at this account is a
   no-op by lookup, not a caught exception. The hazard's real content survives as a rule:
   **the reconciler pre-checks every conflict rather than catching**, the same posture as
   `LockAccountHandler` checking `Status` before calling `Lock()`. That matters more with 1:n, not
   less, because there are now three conflict outcomes instead of one.
4. **Keycloak downtime would stop playing, not just linking.** Every join by an unclaimed account
   calls `FindKeycloakUserIdAsync` *and* `MintLinkPinAsync`, and
   `KeycloakBohemiaGameAccountLinker.GetServiceAccountTokenAsync` caches nothing — four HTTP calls
   per join, each `EnsureSuccessStatusCode`. Wrap the claim-and-mint block for an **existing**
   account in a try/catch: log, degrade to `linkPin`/`linkUrl` null, return the real playable
   status. Only the *creation* path truly needs Keycloak reachable (we must not create a duplicate
   when we cannot check for a binding), and that path returns `Unlinked`.
5. **Ordering constraint, the most consequential one.** The Keycloak binding lookup must stay
   *before* creation. Reversed, a portal-first player who has linked but whose binding the backend
   has not yet observed gets a brand-new second account and silently loses their portal account and
   approved whitelist application. Today's handler already has the right order
   (`CreateSessionCommand.cs:43-55`); creation goes in as the **last resort**, with a comment
   saying why.
6. **The link must not bind on a bare GET.** An attacker who joins the server gets a PIN for
   *their own* game identity, then tricks a logged-in victim's browser into loading the link —
   binding the attacker's game identity onto the victim's account and consuming it (including its
   whitelist approval). **The `kc_action` design answers this for free**: the required action
   renders a confirmation form and binds only on the POST, through Keycloak's own
   `url.loginAction` and form token, which is machinery we neither write nor can get wrong. The
   player also sees what they are linking before confirming. This hazard drove the original
   custom-endpoint design; it is now a property of the flow rather than something to implement.

7. **The link URL is a bearer credential for the player's game identity**, and travels further
   than the PIN did. Whoever reads it inside its TTL binds that game identity to *their own* Keycloak
   account. That was already true of the eight typed characters, but a copy-paste URL is pasted
   into Discord to "open it on my phone", and lands in screenshots and streams intact. Two
   mitigations to decide rather than inherit: have the mod show it on demand instead of
   persistently, and revisit the 30-minute TTL — `BohemiaGameAccountService.PIN_TTL_SECONDS`' own
   javadoc sizes it for the case where "nothing can hand them a fresh one mid-session (it arrives
   once, in the session-bootstrap response)", which is exactly the constraint this change removes.
   Shortening it is therefore only safe once the mod can ask for a fresh URL, so the two decisions
   are coupled.

`SessionStatus.Unlinked` therefore **survives**, narrowed to "identity could not be resolved right
now, nothing was created, retry" (hazard 4). That costs no spec change and keeps the bridge's
`AccountId == null` branch (`bridge/src/Api/Endpoints/SessionEndpoints.cs:35-38`) live.

## Design

### Domain — `src/Accounts/Accounts.Domain/`

`Account.cs`: `KeycloakUserId` → `KeycloakUserId?`; new `AccountId? AbsorbedInto`;
`HasWebAccount => KeycloakUserId is not null`. **`BohemiaId` and `IsLinked` are deleted** — game
identities no longer live on the aggregate, so the property that made `IsLinked` ambiguous between
the two directions is gone rather than documented.

New types alongside it:

- `GameIdentityId.cs` — the rename of `GameId.cs`, still `[StronglyTypedId]` and Guid-backed.
  Its doc comment must say what it is: the per-platform Game Identity, **not** the Bohemia account.
  That distinction is the whole reason this work exists. It should also record that the game's own
  API returns this as a *string* and we parse it to a `Guid` at the boundary deliberately, so a
  non-UUID identity fails loudly as a 400 rather than being stored (see **Unverified**).
- `GamePlatform.cs` — `Pc | Xbox | PlayStation`, nullable at every edge so an unreported or
  unrecognised platform is never an error.
- `GameAccountLink.cs` — a plain document modelled on `GameServer`: `GameAccountLinkId Id`
  (surrogate), `GameIdentityId GameIdentityId` (required natural key), `AccountId AccountId`,
  `GamePlatform? Platform`, `DateTimeOffset LinkedAt`.

A plain document rather than a projection of the link events: the document must be keyed by
`GameIdentityId` while the events belong on the `Account` stream, which would need a
`MultiStreamProjection` — and Marten steers those to the async lifecycle, which would break the
read-your-own-write the reconciler depends on. Writing it explicitly in the same transaction keeps
the inline guarantee and matches `GameServer`'s precedent in the same file.

- `AttachKeycloakUser(KeycloakUserId)` → `KeycloakUserAttached?` — no-op when already that subject
  (the path is re-observed on every join and every `/accounts/me` call), throws for a different one.
- `AbsorbInto(AccountId survivor)` → `AccountAbsorbed` — refuses a second absorb; and **refuses
  when `Status` is `Locked`**. That last guard is what stops the absorb laundering a ban: it moves
  a subject from the loser onto an `Active` survivor, and nothing else would carry the moderation
  state across. It is hard to reach today only because `LockAccountHandler` also disables the
  Keycloak user (`LockAccountCommand.cs:34`), so a banned player cannot authenticate to confirm a
  link — but that is exactly the coupling `docs/accounts.md` and `ARCHITECTURE.md` §4.3 already
  warn is *not* independently load-bearing for the game path, and `session-bootstrap` reads
  `account.Status`, never Keycloak's `enabled` flag. A console-side unlock would open it. Make it
  structural rather than incidental.

  Note the guard that used to carry most of this weight is **gone**: `AbsorbInto` could previously
  refuse structurally on `BohemiaId is not null`, because a loser by definition had no game
  identity. With 1:n that is no longer true — see **The absorb**, where the survivor rule has to be
  rebuilt.
- `BindBohemiaId` is **deleted**, not relaxed. Hazard 3's same-id-rebind throw disappears with it:
  linking is now `GameAccountLinked` on the account stream plus a link document, and re-linking an
  identity already pointing at this account is a no-op by lookup rather than a caught exception.
  The hazard's other half still applies — nothing maps `AccountStatusException` to a problem
  document, so the reconciler pre-checks conflicts rather than catching, the same posture as
  `LockAccountHandler` checking `Status` before calling `Lock()`.
- `Apply(KeycloakUserAttached)` sets the subject; `Apply(AccountAbsorbed)` sets `AbsorbedInto`
  **and nulls `KeycloakUserId`** — that is what keeps `FindByKeycloakUserIdAsync` single-valued.
  Both appends commit in one Marten transaction, so no window exists where two rows hold the subject.

New events in `Events/`: `KeycloakUserAttached(AccountId, KeycloakUserId)`,
`AccountAbsorbed(AccountId, AccountId AbsorbedIntoAccountId)`,
`WhitelistApplicationAccountReassigned(WhitelistApplicationId, AccountId)`,
`GameAccountLinked(AccountId, GameIdentityId, GamePlatform?)` and
`GameAccountUnlinked(AccountId, GameIdentityId)`. The link events exist for audit — "when did this
identity attach, and on what platform" is a moderation question — while the document is what
enforces uniqueness and answers lookups.

`AccountCreated` **drops its game identity** and makes `KeycloakUserId` nullable. The former
`BohemiaIdBound` event is replaced outright by `GameAccountLinked`. Because environments are pruned,
none of this needs a compatibility story — no backfill, no event-type alias, no stored-JSON pins.

The `AbsorbedInto == null` filter goes on the **lookup** finders only —
`FindByKeycloakUserIdAsync` and `SearchAsync`. `FindByIdAsync` stays unfiltered, or the reason for
keeping the loser's stream at all ("what happened to my old id" staying answerable) does not
survive its own design. The game-identity lookup needs no such filter: the absorb re-points link
documents at the survivor, so a link never refers to an absorbed account in the first place.

`SearchAsync`'s identity-substring filter moves onto the link document, since `Account` no longer
holds an identity to match. Its in-memory-filter comment (`:29-32`) carries over unchanged — same
volume assumption, same reason Marten's LINQ cannot translate it.

Prefer a nullable `AbsorbedInto` field over an `AccountStatus.Absorbed` member: `Status` is the
moderation state that `ResolveStatusAsync` and `CreateCharacterCommand.cs:31` key off, and
conflating "absorbed" with "blocked" makes the gameserver's view ambiguous for no benefit.

Projections — add `Apply` to `AccountProjection.cs` (both new account events) and
`WhitelistApplicationProjection.cs`, and extend the warning comment at `AccountProjection.cs:12-14`
to say what now goes wrong (hazard 2), not just "stale".

### Join-first creation — `Accounts.Application/Sessions/CreateSessionCommand.cs`

1. find the **link** by game identity, then its account;
2. if found **and** the account has a subject → `ResolveStatusAsync`, return — fast path, zero
   Keycloak calls;
3. else, inside try/catch: ask the linker for a binding → reconcile if there is one (which may
   create the link against an *existing* account — the second-platform path) → otherwise, if no
   link exists, **create account + link together** in one transaction (last resort, hazard 5) → if
   still unclaimed, mint a PIN (on the 409 → `null`, re-check the binding once and reconcile) and
   build `linkUrl`;
4. on exception: no account → `Unlinked`; account exists → continue, pin and url null (hazard 4);
5. `ResolveStatusAsync` unchanged.

The request also carries an **optional `platform`**, recorded on the link at creation and ignored
when absent or unrecognised. It is metadata only: nothing keys on it, and a null platform is a
normal state, so the mod can start sending it whenever it is ready. Research confirms it is
available server-side — `PlayerManager.GetPlatformKind(int iPlayerId)` is a documented member, and
the server config's own tokens are `PLATFORM_PC` / `PLATFORM_XBL` / `PLATFORM_PSN` — but the script
enum's member names are unconfirmed, so parse unknown values to `null` rather than rejecting.

`ResolveStatusAsync` (`:86-100`) needs **no change**: a brand-new join-first account cannot have a
whitelist application (submission is keyed on the Keycloak subject), so with the hive gate on it
reports `not_whitelisted` automatically. That satisfies the whitelist decision with no new code —
worth a test precisely because it is emergent rather than explicit.

Getting back *out* of `not_whitelisted` is not emergent, though, and does not work without the
portal change: submission needs a subject on the account, and a join-first account has none until
something re-observes the Keycloak binding. See the Portal section — that is where the flow closes.

`CreateSessionResponse`/`SessionDto` gain `bool WebAccountLinked` and `string? LinkUrl`;
`status` stays purely the play-gate and the "show the link" signal rides on those. The three
"null when unlinked" XML doc comments on `SessionDto` all become wrong and need rewriting. One of
them is worse than wrong: `KeycloakUserId`'s comment describes a token exchange the bridge dropped,
and the bridge reads the field nowhere at all (verified by grep across `bridge/src` and
`bridge/tests`). It is now permanently null for an account that has not linked, so say
*retained for compatibility, unused* rather than describing impersonation.

`linkUrl` is built from a **new public-facing Keycloak base URL setting** plus the realm and the
PIN, as the authorization request the Flow section shows — so `ILinkUrlBuilder` also owns the
`client_id`, `redirect_uri`, `response_type`, `scope` and `kc_action` values, and the `link_pin`
parameter name that the SPI reads back out of the auth session. Those two ends must agree, so state
in both places that the name is shared, and keep the SPI's Java test on the note key as the thing
that catches a drift. This cannot reuse `KeycloakOptions.BaseUrl`: that is the in-cluster address the backend dials
(`http://keycloak:8080` on the compose network), while the player's browser needs the published one
(`http://localhost:8180` locally). Add `Keycloak:PublicBaseUrl` and a small `ILinkUrlBuilder` in
the Accounts module. Bind it with
`AddOptions<KeycloakOptions>().Bind(...).Validate(...).ValidateOnStart()` — `required` on
`KeycloakOptions` is compile-time only and reflective binding ignores it, so a missing setting
would otherwise ship a broken URL into the game with no signal.

### The claim, factored once

A plain application service `IAccountIdentityReconciler` (interface and implementation in one file,
following `World.Application/Inventory/IItemCatalogResolver.cs`; registered `TryAddScoped`) — not a
Mediator request, since both callers live in this module and nesting `Send` inside a handler buys
nothing. Input: the bindings Keycloak asserts between a **set** of game identities and a subject
`U` — a set, not a single id, because the claim is now multi-valued. In the table, `A` is the
account reached through the identities' links.

| account(s) by identity | account by `U` | action |
|---|---|---|
| none | — | create account + link |
| none | exists | **link** onto `U`'s account (portal-first, and the second-platform path) |
| one `A`, no subject | — | **attach** `U` to `A` |
| one `A`, subject = `U` | same | no-op |
| one `A`, subject ≠ `U` | any | `Conflict` — never re-point an identity |
| one `A`, no subject | exists, different, **no links** | **absorb** into `A` |
| one `A`, no subject | exists, different, **has links** | `Conflict` — staff only (see below) |
| two or more distinct | any | `Conflict` — staff only (see below) |

The last two rows are new with 1:n and are why the reconciler returns an outcome rather than just
doing the work.

Call sites: `CreateSessionHandler` (replacing `BindFromKeycloakAsync`, `:61-84`) and
`EnsureAccountForKeycloakUserHandler`, which reads the caller's game-identity claim and delegates
when it is present, falling back to today's find-or-create. `ICurrentKeycloakUser` gains
`GetGameIdentityIdsAsync` returning a **list**; `HttpContextCurrentKeycloakUser` reads every value
of the claim and `Guid.TryParse`s each, dropping the ones that fail rather than failing the request.

The realm **does** change here, unlike the single-valued design: the `bohemia-id` client scope's
mapper becomes `multivalued: true` and is renamed with its claim (`gameIdentityIds` attribute →
`game_identity_ids` claim). It stays a **default** scope on `eliferpg-portal`, and its description —
currently "Absent claim means the account is not linked" — stays true, now meaning *no* identities
rather than one.

One property of that to state rather than discover: the claim rides on the **access token**, so it
only appears in a token minted *after* the binding. A player registering during the link bounce is
fine (the portal's auth-code flow runs afterwards), but a player holding a portal session from
before they linked carries a stale token with no claim — and `/accounts/me` then falls through to
find-or-create by subject and creates an empty account. That is not a bug to guard against; it is
precisely the row the absorb reunites on the next request that does carry the claim. Worth saying
so, because it is the second reason the absorb is routine.

Both call sites matter here precisely because the SPI binds without telling the backend: the
`/accounts/me` path claims on the next portal page load, the `session-bootstrap` path on the next
join, and a player who only ever does one of the two is still covered.

### The absorb

**1:n breaks the old survivor rule, and this is the most consequential consequence of the whole
change.** The rule was "the survivor is the one **with** the game identity, because gameservers
have seen its id, and `AbsorbInto` refuses the reverse structurally." With one identity per account
that was airtight: a loser by definition had none.

With many, both sides can have links. Link your PC identity through the portal, then join on Xbox
— which creates a second account carrying the Xbox link — then link Xbox, and the reconciler faces
two accounts that have *both* been handed to gameservers and may both have characters and balances
behind them. Merging them silently destroys one side's.

The rule, therefore:

- **Exactly one side has links** → that side survives, the other is absorbed, its subject and
  whitelist applications carry over, and its links (none) need no re-pointing. This is the common
  case and stays fully automatic — unchanged behaviour from the single-identity design.
- **Both sides have links** → **refuse.** Return `Conflict`, log both account ids and the
  identities involved, and leave it to staff. This module cannot see Characters, Banking or Shops
  state, so it cannot know which side is safe to lose; refusing is the only honest answer, and a
  wrong automatic choice here is unrecoverable.
- **The absorb re-points link documents** at the survivor, which is why the game-identity lookup
  needs no `AbsorbedInto` filter.

A staff-facing resolution path is deliberately **out of scope**: the conflict is rare, it needs a
human to look at downstream data, and designing a merge UI before anyone has hit the case would be
speculative. Logging it with both ids is what makes it actionable.

What makes the common case safe is an invariant to state in the event's doc **as an assumption, not
a fact**: an account with no links has never been returned by `session-bootstrap`, so no gameserver
has seen its id, so nothing downstream references it. The blast radius is narrower than first written:
only **Banking, Characters and Shops** hold an `AccountId` at all (verified by grep — Companies,
Items, Phone and World reach accounts only transitively through Banking), so those three are what
an audit would have to cover. It holds for every production flow, but `POST /api/characters` accepts any `accountId` from any
`gameserver:characters:write` holder, and `docs/accounts.md:43` tells readers to use the
`/accounts/me` id — so a hand-driven local setup can violate it. Keep the loser's stream and record
`AbsorbedInto` so "what happened to my old id" stays answerable, rather than deleting.

**The absorb is a main path, not an edge case**, and the plan should weight it accordingly. Anyone
who signed up on the portal and then joined *before* linking hits it: their join no longer returns
`unlinked`, it **creates** a second account (hazard 5's last resort), so by the time they link there
are two rows and the absorb is the only thing that reunites them. 1:n adds a second routine route
into it — every additional platform a portal-first player joins before linking creates another
account to absorb. Before this change that player
ended up with one account, because the join created nothing. So this is the ordinary
portal-first-then-join-then-link sequence, and the absorb tests are not exotic coverage.

That reframes the carry-over. It is the subject plus any whitelist applications (re-pointed one
event each; the survivor can never already have one, since it had no subject to submit with — which
keeps `FindPendingAsync` and the `AlreadyPending` guard honest). But the loser will **frequently**
have one, because applying before ever joining is "the whole point of the portal-first flow"
(`SubmitWhitelistApplicationCommand.cs:23-24`), and the status that matters most is **`Approved`,
not `Pending`**: `ResolveStatusAsync` gates on `FindApprovedAsync(account.Id)`, so an approved
application left pointing at the loser drops an already-whitelisted player into `not_whitelisted`
on the next join. `IWhitelistApplicationRepository.ListByAccountAsync` must therefore return
applications of **every** status, and the absorb test must assert the approved case, not just the
pending one.

### The SPI browser flow — `keycloak-bohemia-gameaccount`

The in-game URL is an authorization request, so **Keycloak's login flow is the browser flow**. We
add no routes: `BohemiaGameAccountResource` keeps exactly its two trusted-caller endpoints and its
class doc stays true.

`LinkBohemiaGameAccountRequiredAction.requiredActionChallenge` learns to look for the PIN:

```java
String pin = context.getAuthenticationSession()
    .getClientNote(AuthorizationEndpoint.LOGIN_SESSION_NOTE_ADDITIONAL_REQ_PARAMS_PREFIX + "link_pin");
```

When present and well-formed it renders the existing `link-bohemia-gameaccount.ftl` prefilled, as a
confirmation ("link `<gameIdentityId>` to `<username>`?") rather than an empty PIN box; when absent it
renders exactly today's form. `processAction` is **unchanged** — the player still submits, so
`consumePin` → `bind` → the existing `LinkOutcome` mapping all run as they do now. The theme, the
message bundle, `evaluateTriggers` and `initiatedActionSupport` are untouched, and the portal's
existing "Enter my PIN" button (`webapp/server/api/auth/link-game-account.get.ts`) keeps working
because it uses the same `kc_action` with no `link_pin`.

Three consequences worth stating, because each deletes work the earlier design needed:

- **No `LoginFormsProvider` plumbing and no new templates.** The required action already renders
  through the `eliferpg` theme; there is nothing to prove about rendering themed pages from a realm
  resource, because we no longer render from one.
- **No cookie authentication and no CSRF of our own.** The action runs inside Keycloak's
  authentication session, already bound to a user, and the form posts through `url.loginAction`
  with Keycloak's form token. `PIN_PATTERN` also stays where it is — the shape check belongs in
  `requiredActionChallenge` next to the copy that is already there, so no third copy and no
  extraction. `consumePin` keeps being the thing that refuses a malformed value before touching
  storage (`BohemiaGameAccountService.java:78-82`).
- **No login bounce to design.** The authorization endpoint *is* the login page and offers
  registration, which is the common path for a join-first player.

Realm configuration is one new public client rather than a redirect-URI edit on an existing one:
`eliferpg-game-link`, `redirectUris: ["/realms/eliferpg/bohemia-gameaccount/*"]`, no secret. The
authorization code it returns is **never exchanged** — the redirect exists only to land the player
on a "done" page — so there is no state, no PKCE verifier and no secret anywhere in this flow. A
dedicated client also keeps `eliferpg-portal`'s redirect URIs untouched, which matters because that
client is confidential and carries the game-identity scope.

**`bind` becomes additive, which is the SPI's share of the 1:n change.**
`BohemiaGameAccountService.bind` currently calls `setSingleAttribute` and returns `CONFLICT` when
the user already holds a *different* value (`:94-100`) — that refusal is exactly the 1:1 assumption
this change removes. It becomes:

- the attribute is **multi-valued** (`gameIdentityIds`). It must be written with
  `setAttribute(name, List)` as a **read-append-write**: `setSingleAttribute` goes through
  `removeAttribute` plus a single insert and would **wipe the user's other identities**. Append only
  when absent — `UserAdapter.setAttribute` does not de-duplicate the list it is handed, though it
  does short-circuit when the value *set* is unchanged, so a no-op re-link costs nothing;
- already present on this user → `ALREADY_LINKED`, as today;
- held by **another** user → `CONFLICT`, as today. This is the uniqueness guarantee that matters,
  and it survives the move to multi-valued — see **Unverified**, where this is now proven rather
  than assumed;
- otherwise → **append** and return `LINKED`.

Keep the lookup on `session.users()` rather than switching to
`UserStoragePrivateUtil.userLocalStorage()`. For a *uniqueness* check the dangerous error is a
false negative — missing an existing holder and letting two accounts claim one identity — and
`session.users()` fans out across every user provider, so it is the broader and therefore safer
view. The realm has no user federation configured (`userFederationProviders: null`, no
user-storage component, and Discord is identity *brokering*, which creates ordinary local users),
so today the fan-out reduces to the JPA provider exactly. Adding user federation later would be the
trigger to revisit this.

Two further properties worth writing down because they are load-bearing and invisible:

- **Matching is exact and case-sensitive** under Postgres collation (the query is a plain `=` with
  no `LOWER()`). That makes the canonical-form chain load-bearing: the backend parses the identity
  to a `Guid` and re-serialises with `ToString()`, which is lowercase hyphenated, and the backend is
  the only writer — the SPI stores what it was handed and `consumePin` returns it unchanged. If
  anything ever writes a non-canonical casing, two spellings of one identity become two distinct
  attribute values and uniqueness silently breaks.
- **Uniqueness is per realm**, since the query filters on `realmId`. One realm, so this is a
  non-issue — but it is an assumption, not a guarantee.

`LinkOutcome` needs no new member, so `processAction`'s mapping and the message bundle are
untouched. The service's class doc, which says linking is "a single attribute write on the caller",
needs the word *single* removed. Its "no second user, no merge and no deletion" claim stays true of
Keycloak and is already flagged as no longer true of the backend.

The one coupling to name: `LOGIN_SESSION_NOTE_ADDITIONAL_REQ_PARAMS_PREFIX` is `public static final`
but internal-ish Keycloak API, in the `keycloak-services` artifact the SPI already depends on at
`provided` scope — the same risk posture as its existing use of `AppAuthManager`. Pin it with a
Java test asserting the note key, so a major upgrade fails loudly in CI rather than silently
dropping back to an empty PIN box.

### Portal — required, and it is what completes the flow

The portal never calls `POST /api/accounts/me` at all today (verified by grep across `webapp/`), so
a freshly-registered user has a Keycloak user and no backend account. That reads like a
pre-existing portal-first bug to defer, and it was one — but this change makes it **the thing that
blocks join-first end to end**, so it ships here.

The chain: `SubmitWhitelistApplicationHandler` resolves the account from the caller's subject
(`SubmitWhitelistApplicationCommand.cs:43`) and returns `AccountNotFound` when there is none. A
join-first player who has linked has a Keycloak user carrying `bohemiaId` and a backend account
with **no subject** — the binding lives only in Keycloak until something re-observes it, and on a
portal visit nothing does, because nothing calls `/accounts/me`. So whitelist submission 404s. With
the hive gate on, `not_whitelisted` is exactly where a join-first player lands and the application
is their only way out of it: the headline flow dead-ends.

The fix is one call: `POST /api/accounts/me` on authenticated page load (or in the auth callback).
The endpoint is already built, already idempotent, and already documented for precisely this
(`AccountEndpoints.cs:95-96`) — it has no caller. Going through it also runs the reconciler's claim
path, so the join-first player's attach happens on their first portal page view rather than waiting
for their next join.

Consequence for rollout: **hive whitelisting must stay off until the portal ships**, or join-first
players are stranded between `not_whitelisted` and an application they cannot submit.

**The portal's share is bigger than that one call**, though — verified by grep, and larger than an
earlier draft of this plan assumed. Both the rename and the multi-valued claim reach it:

- `shared/types/accounts.ts` (`bohemiaId: string | null`) and
  `app/components/AccountsTable.vue` (`accessorKey: 'bohemiaId'`, header `"Bohemia ID"`) consume
  the **admin accounts response**, which the rename changes. The column should become a count or a
  list, since an account now has *n* identities; a single-value column cannot represent the new
  model even after renaming. `app/pages/admin/accounts.test.ts` fixtures follow.
- `server/api/auth/callback.get.ts` reads the `bohemia_id` claim into `bohemiaId`, and
  `shared/types/auth.d.ts` + `app/composables/useAuth.ts` derive
  `isLinked = Boolean(user.value?.bohemiaId)`. With the claim multi-valued, `isLinked` becomes
  "holds at least one identity" and the type becomes a list. The existing comment in `useAuth.ts`
  about degrading to `false` for an unlinked user stays correct and still matters.
- `server/api/auth/link-game-account.get.ts` keeps working untouched — it uses `kc_action`, which
  is unchanged — but its comment references the old claim name.

## The rename

`bohemiaId` → `gameIdentityId` across the backend domain, application and api, the OpenAPI
contract, the Keycloak user attribute and claim, the SPI, the bridge, the mod and the docs. The
provider artifact name (`keycloak-bohemia-gameaccount`), its realm resource path and the
`link-bohemia-gameaccount` required-action alias **stay** — renaming those means a realm
re-registration and a new jar coordinate for no behavioural gain, and the alias is baked into the
portal's existing `kc_action` link.

This is the **only breaking part of the whole change**; 1:n itself is additive on the wire, since a
join still carries exactly one identity. So the rename is what forces the api-client **major**, and
it is worth doing now rather than later precisely because the alternative is shipping a second
breaking train for a pure naming fix.

Carry-through: the bridge's `PlayerConnectedRequest`, `PlayerDisconnectedRequest` and
`CharacterSelectedRequest` records and `PlayerSessionTracker`'s keys rename; the mod's request
bodies change. The Keycloak user attribute renames with everything else; since the realm's users
are pruned too, no Admin API copy pass is needed.

## Steps

Repo stays runnable at every step.

1. **Rename, mechanically and on its own.** ✅ *Done — see the note below.* `GameId` →
   `GameIdentityId`, `BohemiaId` → `GameIdentityId` through domain, application, api and tests
   (docs deliberately excluded; step 6 rewrites them). No behaviour change, one commit.
   `GameIdentityId` **stays Guid-backed** — the console format question is closed by research.

   Because environments are pruned, persisted names follow the C# ones and nothing needs pinning:
   the event type `BohemiaIdBound` is renamed to `GameIdentityBound` along with its member, and no
   `[JsonPropertyName]` compatibility attributes survive. `grep -r BohemiaId src/ tests/` returns
   nothing. Verified: `dotnet build` clean (0 errors, no new warnings) and
   `tests/Accounts.Domain.UnitTests` 27/27.
2. **Domain.** `GameAccountLink`, `GameAccountLinkId`, `GamePlatform`, the two link events;
   `Account` loses `GameIdentityId` and `IsLinked`; `AccountCreated` loses its game identity;
   `GameIdentityBound` is replaced by `GameAccountLinked`. Then the other three new event files,
   both projections,
   `WhitelistApplication.cs`. The compiler then forces the five staff call sites —
   `LockAccountCommand.cs:34`, `UnlockAccountCommand.cs:32`, `AccountRolesQuery.cs:25`,
   `AssignAccountRoleCommand.cs:27`, `RevokeAccountRoleCommand.cs:27`. Lock/unlock skip the
   Keycloak call and still record the event (locking a join-first account must work — it is the ban
   button); the three role endpoints get a `NoWebIdentity` result case mapped to **409**, which is
   honest where a 404 would lie to staff. Unit tests alongside.
3. **Infrastructure**: the `UniqueIndex` registration beside `GameServer`'s,
   `IGameAccountLinkRepository` + `MartenGameAccountLinkRepository`
   (`FindByGameIdentityAsync`, `ListByAccountAsync`, `Insert`, `Repoint`), the null guard on
   `FindByKeycloakUserIdAsync`, the `AbsorbedInto == null` filter on the lookup finders only,
   `IWhitelistApplicationRepository.ListByAccountAsync`, and `SearchAsync` moving onto the link
   document.
4. **The reconciler** over a *set* of identities, wired into `CreateSessionHandler` and
   `EnsureAccountForKeycloakUserHandler`; `ICurrentKeycloakUser.GetGameIdentityIdsAsync`;
   `TestCurrentKeycloakUser` gains settable identities. Includes the three `Conflict` outcomes:
   both-sides-linked, split accounts, and a `Locked` loser. Still returns `unlinked` for an unknown
   identity, but the link, attach and absorb now all work.
5. **Optional hardening**: cache the service-account token in `KeycloakBohemiaGameAccountLinker`
   (same 30s-margin pattern as the bridge's `GetOwnTokenAsync`); delete the dead
   `KeycloakUserRepresentation` at `KeycloakUserProvisioner.cs:151`.
6. **Docs**, before the cross-repo train so the ripple PRs can link to them.
7. **SPI**: the multi-valued attribute and additive `bind`, plus
   `requiredActionChallenge` reading `client_request_param_link_pin` off the
   auth session and prefills the existing form — plus Java tests and new cases in
   `scripts/integration-test.sh`. Then the `eliferpg-game-link` client, the jar release and the
   `ghcr.io/eliferpg/keycloak` rebuild. Much smaller than first scoped: no new routes, no new
   templates, no `LoginFormsProvider` work, no cookie or CSRF handling.
   **Deliberately before the creation step**, not after. The SPI is additive and depends on nothing in the
   backend, whereas the reverse order opens a window in which every join-first player is handed a
   URL that 404s — and their only recovery in that window is *register on the portal, then type the
   PIN*, which is the detour this whole change exists to delete. Shipping the SPI first costs
   nothing but a Keycloak image carrying routes nobody calls yet.
8. **Join-first creation + `linkUrl` + the optional `platform` field**: `Keycloak:PublicBaseUrl`,
   `ILinkUrlBuilder`, the handler change, `SessionDto`, `appsettings.Development.json`,
   `TestServices.cs` config (**one `AddMediator` call site only** — a second fails the build).
   Rewrite the affected tests, then `dotnet build` regenerates
   `openapi/eliferpg-api-v1.json`; commit it.

   Environment gotcha worth recording in the repo docs, pre-existing and unrelated to this change:
   `src/Api/Api.csproj` sets `OpenApiGenerateDocumentsOnBuild=true`, which boots the host at build
   time, so **`dotnet build` needs `ASPNETCORE_ENVIRONMENT=Development`**. Without it Marten gets a
   null connection string and the build fails with ~21 confusing errors out of
   `Microsoft.Extensions.ApiDescription.Server.targets` rather than anything pointing at the cause.
9. **Client + bridge + portal + mod** (below).

## Ripple

The spec diff is **breaking, and for once the tooling will say so.** The rename of `bohemiaId` →
`gameIdentityId` is a genuine contract break, so `oasdiff` flags it and `backend-api-client` cuts a
**major**. Note it breaks in **two** places, not one: `CreateSessionRequestDto` (the request, which
the bridge and mod send) and `AccountDto` (the admin accounts *response*, which the webapp renders).
An earlier draft listed only the request. The generated client's
`Generated/Models/CreateSessionRequestDto.cs` and `Generated/Models/AccountDto.cs` both change. Everything else is additive: `SessionDto` gains
`WebAccountLinked`/`LinkUrl`, the request gains optional `platform`, three endpoints gain a 409, and
`status` stays an unconstrained `{"type":"string"}` with no enum (verified).

The *behaviour* is breaking in ways the diff still will not show: `unlinked` effectively
disappears, `accountId` is now always set, `linkPin` appears alongside `active`/`not_whitelisted`,
and one account can now answer for several identities. This is the trap the api-client README
records for backend #147 — say so in the backend PR body, because even a major's generated
changelog will only mention the rename.

Order: backend merge → `dispatch-client-generation.yml` fires → api-client releases → bridge bumps
deliberately. In the bridge: keep the `AccountId is null` early return as the `Unlinked`/defensive
path but **rewrite its comment**, which currently asserts accounts are never created by joining;
add `LinkUrl`/`WebAccountLinked` to `PlayerConnectedResponse` (both construction sites, the XML doc,
and the two `tests/Api.UnitTests` classes whose constructor calls change); rename across the three
request records and `PlayerSessionTracker`'s keys; rewrite the `.WithDescription` at
`SessionEndpoints.cs:58`, and make sure **`not_whitelisted` also carries the link URL** — that is
the state a join-first player lands in with whitelisting on, and the URL is their only way out.
Ignore the stale `reforger-bridge` checkout.

**The mod** then renames its request field, consumes `linkUrl` for copy-to-clipboard, and starts
sending `platform` from `PlayerManager.GetPlatformKind`. No Enfusion source is checked out in any
local repo (no `.c` files anywhere under `~/dev/projects/eliferpg`), so this part cannot be scoped
from the backend side and needs tracking separately — it is also the only place the rename is not
mechanical, because the mod is the one caller that must change in lockstep with the major.

**The portal ships in this train too**, not after it — the single `POST /api/accounts/me` call from
the Portal section. It is independent of the api-client bump (the endpoint and its generated method
both already exist), so it can go in parallel with the bridge rather than behind it. It is the last
piece of the join-first flow: until it lands, a join-first player who has linked still cannot
submit a whitelist application.

## Verification

- `dotnet test tests/Accounts.Domain.UnitTests` — pure. New: attach guards, the absorb guards
  (second absorb, `Locked` loser), the link/unlink events, and replay tests.
  `BindBohemiaId_WhenAlreadyLinkedToTheSameId_Throws` is **deleted** along with the method it
  covers — the behaviour it asserted is now "the reconciler does not call this at all".
- `docker compose up -d`, then `tests/Accounts.IntegrationTests` against the real Postgres and
  Keycloak (the suite requires the SPI installed in the realm). Rewrite
  `CreateSessionCommandTests.cs:40` (unknown ID now creates and returns `active` with a url) and
  `:53` (fresh PIN per join, now on the *same* account); `:64` and `:82` survive. Add: attach when
  Keycloak reports a binding for a subject-less account; the absorb, asserting one survivor, the
  loser's `AbsorbedInto`, and that `FindByKeycloakUserIdAsync` returns one row **without throwing**
  (the hazard-1 regression test); a throwing linker double still returning a playable status.
- New `CreateSessionCommandWhitelistGateTests` cases: unknown ID with the gate on →
  `not_whitelisted`; then the full join-first → link → apply → approve → `active` loop. **This
  test does not cover the portal gap** and must say so in a comment: it drives the handler with a
  `TestCurrentKeycloakUser` that supplies a subject, which in production only exists once something
  has called `/accounts/me`. It would have gone green against the original design, in which the
  flow was broken end to end. The real check is the by-hand portal pass below.
- New `EnsureAccountForKeycloakUserCommandTests` — the endpoint has **zero** coverage today:
  create-when-no-claim, idempotence, claim-matching-a-join-first-account attaches, claim-with-an-
  existing-empty-account absorbs (with a pending application re-pointed, **and separately an
  approved one** — that is the case whose failure silently un-whitelists a player), a `Locked`
  loser refused, claim nobody owns.
- New `AccountProjectionTests` — the only class that catches a missing projection `Apply`
  (hazard 2): append each new event through the repository, save, re-read through the finders.
- New `GameAccountLinkTests` (integration), the 1:n core:
  **two concurrent `CreateSessionCommand`s for one identity yield exactly one account** — now a
  real assertion against the unique index rather than a mitigation, and the hazard-1 regression
  test; a second identity linking onto an existing account; the same identity refused for a second
  account; `ListByAccountAsync` returning both links; a link surviving the absorb by re-pointing;
  and a link surviving a re-link of the same identity as a no-op.
- `CreateSessionCommandWhitelistGateTests` gains: a player joining on two platforms reaches
  `active` on both through **one** approval — the whole point of 1:n, and the case that would break
  silently if the absorb or the re-pointing were wrong.
- Absorb cases, the ones 1:n adds: links on one side only → absorbs; links on **both** sides →
  refused with both ids logged; identities split across two accounts → refused.
- `LockAccountCommandTests`/`AccountRolesCommandTests`: the no-web-identity cases.
- Java: `bind` appends to the multi-valued attribute rather than replacing — the regression test
  that matters, since `setSingleAttribute` silently wipes the user's other identities;
  `ALREADY_LINKED` for a value this user holds; `CONFLICT` for one another user holds. Plus one
  end-to-end case in `scripts/integration-test.sh`: a user holding two identities is still found by
  a search for *either*. That behaviour is now proven from the source rather than unknown, so the
  test is a guard against a future Keycloak change, not a discovery. Then unit tests on `requiredActionChallenge` — prefills from the note, falls back to today's
  empty form when the note is absent, ignores a malformed note value, and (the upgrade canary) one
  test asserting the note key is literally `client_request_param_link_pin`, so a Keycloak major
  that moves `LOGIN_SESSION_NOTE_ADDITIONAL_REQ_PARAMS_PREFIX` fails CI instead of silently
  degrading to a blank box. `processAction` needs no new tests — it is unchanged.
  `scripts/integration-test.sh` gains the end-to-end cases: link with an existing Keycloak session,
  **link as a brand-new user who registers mid-flow** (the one genuinely open question — it proves
  the client note survives registration), a replayed link, and a conflict.
- By hand, both orders, per `docs/accounts.md`: join an unknown game identity → account plus
  `linkUrl`; open the link in a browser with no Keycloak session, register, confirm, and check the
  attribute persisted; re-bootstrap → same `accountId`, now with a subject. Then the portal-first
  order, unchanged.
- By hand, **two platforms**: join on PC → link → join from a second platform → confirm one
  `accountId`, two links, and the account listing showing both.
- By hand, **the gate**, which is the pass no automated test substitutes for: with
  `WhitelistEnabled` on, go join-first → link → *open the portal* → submit an application →
  approve → rejoin and get `active`. Running it before the portal change should reproduce the
  404 at submission; after, it should pass.

## Rollout

**Prune the volumes** — `docker compose down -v`, and wipe the Keycloak realm's users along with
them. That is the agreed rollout, and it is what licenses the rest of this design to ignore data
compatibility entirely: persisted field names, the renamed event type and the Keycloak attribute
all change freely, and `GameAccountLink`'s unique index is created against an empty table. Nothing
here is recoverable afterwards by reverting code, which is fine by the same decision — but it does
mean the rollout is one-way, so it should land as a deliberate step rather than as a side effect of
a deploy.

**Two realm-export changes:** the new public client `eliferpg-game-link`, with
`redirectUris: ["/realms/eliferpg/bohemia-gameaccount/*"]` and no secret; and the `bohemia-id`
client scope's mapper becoming `multivalued: true` with the renamed attribute and claim
(`gameIdentityIds` → `game_identity_ids`). Everything else needs none — the required action is
already registered, the scope is already a **default** one on `eliferpg-portal`, and
`registrationAllowed` is already true (all verified in
`hosting/dev/keycloak/import/eliferpg-realm.json`). Since the realm re-imports only on
a fresh volume, the README note must give the Admin API or console equivalent for an existing one.

**Keycloak container must be recreated** once `ghcr.io/eliferpg/keycloak` carries the new provider
jar (`docker compose pull keycloak && docker compose up -d --force-recreate keycloak`). Skipping it
degrades more gently than the earlier design would have: the URL still reaches Keycloak, still logs
the player in, and still runs the required action — they just get today's empty PIN form and type
the PIN that is already on their screen, instead of a prefilled confirmation. Nothing 404s. The
`eliferpg-game-link` client is the one piece that must exist first, or the authorization request is
rejected outright. Note too that restarting
Keycloak invalidates in-flight PINs, so a mid-session player must rejoin for a fresh one.

**New required setting** `Keycloak:PublicBaseUrl` — committed for Development, fails fast at
startup elsewhere. Add the README "Rollout note" paragraph the repo keeps per change.

**Keep hive whitelisting off until the portal ships.** The order that works is SPI → backend →
portal, and only after the portal's `/accounts/me` call is live can a join-first player get out of
`not_whitelisted`. Turning the gate on earlier strands exactly the players this change was written
for. This is the one ordering constraint in the rollout that is not self-correcting.

Docs to correct: `docs/accounts.md` (the "`session-bootstrap` never creates anything" claim at
`:15-19` becomes exactly backwards; the status table; the two "consequences" bullets at `:74-75`;
the dead `BridgeTokenProvider.ExchangeForPlayerTokenAsync` citation at `:92`; and the already-stale
whitelist section at `:148-153`, which still says `gameserver:whitelist:write` and passes
`accountId` — the endpoint has required `account:self:manage` since `WhitelistEndpoints.cs:17`),
the two stale endpoint descriptions at `Accounts.Api/Sessions/AccountEndpoints.cs:74` and `:116`,
`ARCHITECTURE.md` §4.3 (its "every player needs an actual Keycloak user record" is now doubly
false), and a dated superseded note in `MIGRATION.md` §5 rather than rewriting history. The SPI
README documents the `kc_action` URL shape and the `link_pin` parameter — including that the
parameter name is shared with the backend's `ILinkUrlBuilder`, since that is the kind of coupling
nobody finds from one side. Its "no second user, no merge and no deletion" service doc is still
true of Keycloak but no longer of the backend, so it needs a pointer.

## Unverified — one left, and it does not block

The three original Keycloak-internals questions (SSO-cookie auth inside a
`RealmResourceProvider`, themed rendering via `LoginFormsProvider`, and the login bounce) are gone
with the custom endpoints that raised them. 1:n added two of its own, and both are now resolved —
the Game Identity format by research, the multi-valued attribute search by reading Keycloak's
source. One question remains, and it degrades gracefully rather than blocking.

**Resolved by research — the Game Identity ID is a UUID on every platform**, so `GameIdentityId`
stays Guid-backed and the rename does not become a type change. An earlier draft made this a
blocking step 0; it is closed. The evidence:

- BI's own Enfusion Script API gives
  `proto external string BackendApi.GetPlayerIdentityId(int iPlayerId)` — the *return type* is a
  string, and BI never documents the format beyond that.
- The format is documented third-party as "a UUID, one per game-platform combination, created on
  the first connection", and shows up that way in `PlayerData.<identityId>.json` profile filenames
  and in the `UUID = ` label on the join line in `console.log` — a method one guide notes works
  "on any platform".
- The apparent contradiction is explained rather than unresolved. A hosting panel's identity box
  accepting "letters, digits and dashes, 8 to 64 characters" looked like evidence against a UUID,
  but `game.admins` accepts **identityIds and/or Steam64 IDs**, so that box has to admit both a
  36-character UUID and a 17-digit Steam64. Its permissiveness says nothing about the identity
  format. Likewise, one guide calling the identity "the lasting ID of a player's Bohemia account"
  simply contradicts the wiki's per-platform model and is wrong on that point.
- Independently, this backend already parses PC identities as `Guid` and works, and the per-platform
  identities are minted by the same Bohemia backend, so there is no mechanism by which a console
  one would differ in shape.

Residual risk, and why it is acceptable: no BI document states the format, so this is inference
from converging third-party technical sources. The failure mode is **safe and loud** — our wire
contract is a `Guid`, so a non-UUID identity fails ASP.NET model binding with a 400 at the edge
rather than being stored or silently truncated. We would learn about it on the first console join,
having corrupted nothing. That is why this no longer gates the work.

**Resolved by reading the 26.0.8 source — `searchForUserByUserAttributeStream` does match any
single value of a multi-valued attribute**, so the uniqueness check survives the move. Evidence,
from `org.keycloak.keycloak-model-jpa-26.0.8.jar`:

- `UserEntity`'s named query `getRealmUsersByAttributeNameAndValue` is
  `select u from UserEntity u join u.attributes attr where u.realmId = :realmId and attr.name = :name and attr.value = :value`
  — an **inner join over a one-to-many**, so one matching row returns the user. No `LOWER()`, no
  truncation.
- `UserAttributeEntity` is its own `@Entity` on `USER_ATTRIBUTE` with a **surrogate PK per value**,
  and `UserAdapter.setAttribute` persists one entity per list element. Nothing is concatenated.
- Corroborating, the Liquibase history in the same jar shows the original
  `(NAME, USER_ID)` primary key being **dropped** in `jpa-changelog-1.4.0.xml` and replaced with one
  on `ID` — multiple rows per attribute name are legal by deliberate design, not by accident.
- Values over 255 characters take a hashed `LONG_VALUE` path with an any-value post-filter, so it
  holds there too; a 36-character UUID stays on the simple `VALUE` column regardless.

- **Does the `client_request_param_link_pin` note survive a brand-new user registering mid-flow?**
  The auth session persists across the registration form, so it should, but a registration path
  that starts a fresh auth session would drop it. This is the common path for a join-first player,
  so it is the one case that must be proven rather than reasoned about —
  `scripts/integration-test.sh` against the local Keycloak 26.x, as a scripted
  register-during-link run.

If it does not survive, the degradation is already the fallback: the player lands on today's empty
PIN form with the PIN still on their game screen, so they type it. One extra step, nothing broken,
and the typed path is tested either way.

Settled by inspection rather than left open (all read out of
`quay.io/keycloak/keycloak:26.0.8` directly): the note prefix is `client_request_param_`; unknown
params are extracted up to 5 and 2000 chars; `link_pin` is not in `KNOWN_REQ_PARAMS`;
`RequiredActionContext.getAuthenticationSession()` exists; and, had the custom-endpoint design
survived, `AppAuthManager.authenticateIdentityCookie(KeycloakSession, RealmModel)` is the 26.x
instance method (with a static three-arg overload alongside).

The unique-index question that an earlier draft left open — whether Marten can express
`UniqueIndex` on a *nullable strongly-typed id* — is **closed by not arising**: the key is now a
plain required field on `GameAccountLink`, which is the shape `GameServer.ClientId` already proves
works in this very module. Still genuinely open: whether this compose setup's
`SingleUseObjectProvider` is DB-backed, i.e. whether a Keycloak restart really drops PINs (assume
it does).
