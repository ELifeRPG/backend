# Hive tenancy: characters across multiple servers

> **Supersedes `2026-08-15-multi-gameserver-tenancy-design.md`.** That spec
> scoped `Characters`/`Banking`/`Companies`/`Items`/`Shops` per gameserver
> using Marten conjoined multi-tenancy. This spec removes that isolation
> entirely. Read this one for the current model.

## Context

New product requirement: one deployment of ELifeRPG is **one "hive"**
containing multiple game servers, where **one server = one map**. A
character persists which server it is on plus its location on that server,
and will later be able to **travel** between servers.

Today's model is the opposite. `2026-08-15-multi-gameserver-tenancy-design.md`
scopes five modules per gameserver via Marten conjoined multi-tenancy, keyed
on the calling gameserver's OAuth `client_id` claim
(`ICurrentGameServer`, implemented per module). A character created via one
gameserver is invisible from another — that spec's own words: *"'wrong
server' and 'doesn't exist' become indistinguishable by construction, which
is the right behavior across a trust boundary."* Five integration tests
assert exactly that invisibility.

Under the hive model there is no trust boundary between servers — they are
maps in one shared world. **Tenancy exists to isolate; a hive needs
labeling, not isolation.** A shop on Map A is not a security boundary, it is
a fact about where the shop is. Isolation is expensive here: `tenant_id`
forms half the composite primary key that three raw-SQL row locks depend on
(ARCHITECTURE.md §9e gotcha 9), plus per-module `ICurrentGameServer` wiring.
A label is a field. So conjoined tenancy comes out, replaced by `ServerId`
attributes on the few things that are physically placed.

## Goals

- All gameplay data is hive-wide: a character, its skills, money, companies,
  and the item catalog are reachable from every server in the deployment.
- A character persists which server it is currently on, and its location on
  that server, updated by a periodic heartbeat.
- The data model does not foreclose travel, and the invariant that makes
  travel safe (a character cannot be live on two servers at once) is
  enforced from day one.

## Non-goals

- **Travel orchestration.** Depart/arrive events and endpoints, Bridge
  coordination, arrival points, crash-mid-travel recovery. The state machine
  is specified below so the follow-up has it, but no `TravelState` field is
  added — see "Deferred."
- **Multiple hives per deployment.** One deployment is one hive. If that
  ever changes, the tenancy machinery being removed here is what would come
  back, so this decision is worth revisiting deliberately rather than
  hedging now.
- **Stale-session reaping.** This change produces the signal
  (`LastHeartbeatAt`) that makes it possible; acting on it is separate work.
- **Data migration.** There is no production deployment. Removing
  `tenant_id` from composite primary keys is a table rebuild; rollout is a
  volume wipe (`docker compose down -v`), the same rollout the superseded
  spec used.

---

## Part 1 — Removing conjoined tenancy

### Store configuration

Drop from all five `*.Infrastructure/ServiceCollectionExtensions.cs`
(`Characters`, `Banking`, `Companies`, `Items`, `Shops`):

```csharp
options.Events.TenancyStyle = TenancyStyle.Conjoined;
options.Policies.AllDocumentsAreMultiTenanted();
```

Note the implementation used the blanket `AllDocumentsAreMultiTenanted()`
policy rather than per-type `MultiTenanted()`, so this is a two-line removal
per store rather than a per-aggregate audit. `Accounts` is already
untenanted and unaffected.

### Session opening

Eight Marten repositories open sessions as
`store.LightweightSession(currentGameServer.ClientId)`; these become
`store.LightweightSession()`:

`MartenCharacterRepository`, `MartenCharacterSkillsRepository`,
`MartenBankRepository`, `MartenBankAccountRepository`,
`MartenCompanyRepository`, `MartenItemRepository`, `MartenShopRepository`,
`MartenShopListingRepository`.

The three cross-module repository factories
(`MartenBankAccountRepositoryFactory`, `MartenCompanyRepositoryFactory`,
`MartenShopListingRepositoryFactory`) drop `options.TenantId = ...` from
their `SessionOptions.ForTransaction(...)` setup, and stop threading the
tenant id into the repository constructor as a second plain-string argument.

### Row locks — the highest-risk part of this change

Three `SELECT ... FOR UPDATE` statements stand in for Marten's optimistic
concurrency on `ForTransaction`-bound sessions (§9e gotcha 9):

```sql
SELECT id FROM banking.mt_doc_bankaccount WHERE tenant_id = @tenant AND id = @id FOR UPDATE
```

...and equivalents against `companies.mt_doc_company` and
`shops.mt_doc_shoplisting`. The `tenant_id` predicate is not incidental —
gotcha 9 states explicitly that both columns of the composite
`(tenant_id, id)` primary key must be filtered *"or the query seq-scans the
table."* With conjoined tenancy removed the primary key becomes `id` alone,
so the single-column predicate should be the index lookup — but this must be
**verified with `EXPLAIN`, not assumed.** A silent regression here degrades
a correctness mechanism into a table scan under load.

Update §9e gotcha 9's documented SQL to match, keeping its warning about
filtering the full primary key intact.

### SignalR

`Shops.Api/ShopsHub.cs` reads `client_id` directly (not via
`ICurrentGameServer`) and refuses to join a group for a shop outside the
caller's tenant, silently no-op'ing so the caller learns nothing about other
tenants. Under the hive model shops are hive-visible, so this check is
removed rather than rewritten.

### Tests

Five integration tests assert cross-server invisibility and now assert the
opposite of the requirement. They are **inverted, not deleted** — the
visibility they cover is exactly what this change is for:

- `Characters.IntegrationTests/CreateCharacterCommandTests.cs`
  (`Handle_CharacterCreatedUnderOneServer_IsInvisibleFromAnotherServer`)
- `Banking.IntegrationTests/BankingCommandTests.cs`
- `Companies.IntegrationTests/CompanyCommandTests.cs`
- `Items.IntegrationTests/ItemCommandTests.cs`
- `Shops.IntegrationTests/ShopCommandTests.cs`

`TestServices.BuildProvider(string gameServerClientId)` and the
`FixedCurrentGameServer` fake stay — the two-provider setup is still how a
"different calling server" is expressed, it just no longer implies
partitioning.

---

## Part 2 — Server registry with a real identity

### `GameServerId`

Today the tenant key, the Keycloak credential name, and the `GameServer`
document identity are all the same string: the OAuth `client_id`. The
superseded spec flagged promoting this to a GUID value object as desirable
but deferred it, reasoning *"the tenant id Marten needs is a string either
way."* That reasoning expires here: server identity stops being a hidden
Marten key and becomes durable domain data referenced by character rows. If
that reference is an OAuth client name, rotating or renaming a gameserver's
Keycloak client silently orphans every character on that map.

Add `GameServerId` as a `[StronglyTypedId]` GUID in `Shared.Kernel`,
alongside `AccountId`/`CharacterId`/`CompanyId`/etc.

### `GameServer`

Grows from `{ ClientId, WhitelistEnabled }` to:

```csharp
public sealed class GameServer
{
    public required GameServerId Id { get; init; }
    public required string ClientId { get; init; }   // Keycloak mapping, no longer the identity
    public string DisplayName { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
}
```

Marten identity moves from `ClientId` to `Id`, with a unique index on
`ClientId` for claim resolution. `Id` and `ClientId` are `init` — a server's
identity and its Keycloak binding are fixed at registration; `DisplayName`
and `MapName` are mutable, and become what the existing `PATCH` endpoint
edits now that `WhitelistEnabled` has left (see below).

### `ICurrentGameServer`

Survives, but changes meaning: from *tenant key* to *which server is
calling*. It resolves the request's `client_id` claim to a `GameServerId`
through the registry, and keeps the existing fail-closed throw on a missing
claim. It is still declared once per module (§9e: modules don't share
Application-layer ports).

Note this adds a registry lookup to a path that was previously a pure claim
read. Cache it per request — it is already resolved lazily per repository
construction, and a scoped service holding the resolved value is sufficient.

### Registry endpoints — closing a real gap

`GameServerEndpoints` today has only `GET` and `PATCH` by `clientId`, and
`MartenGameServerRepository.GetOrDefaultAsync` returns an unpersisted
default for an unknown client id — meaning a "game server" springs into
existence implicitly the first time settings are written, and there is no
way to enumerate servers at all.

A hive needs an explicit, enumerable server list (travel will need to name
destinations, and the portal will want to show players where they are). Add:

- `POST /api/game-servers` — register a server explicitly
  (`ClientId`, `DisplayName`, `MapName`), `server-admin` role.
- `GET /api/game-servers` — list all servers in the hive.

Drop the implicit-default behavior from `GetOrDefaultAsync`: an unknown
`client_id` is now an error, not a silently-defaulted server. This is a
behavior change for any caller relying on the default —
`CreateSessionCommand` is the only one.

### Whitelist moves to hive level

Approved once, play anywhere. Remove `ServerClientId` from
`WhitelistApplication`, `WhitelistApplicationSubmitted`, and
`SubmitWhitelistApplicationCommand`. `WhitelistEnabled` becomes a single
hive-level setting rather than a per-`GameServer` flag.

**Where a hive-level setting lives.** There is no "hive" entity today, and
this is the first setting that needs one. Rather than invent a `Hive`
aggregate for a single boolean, add a singleton `HiveSettings` document in
`Accounts` (fixed well-known id, `{ WhitelistEnabled: bool }`), with
`GET`/`PATCH /api/hive/settings` gated on the `server-admin` role — the same
gate `GameServerEndpoints` already uses. A document rather than
configuration because it is admin-editable at runtime today via
`PATCH /api/game-servers/{clientId}`, and that capability should not
regress into a redeploy.

If a second hive-level setting appears later, promoting this to a proper
aggregate is a contained change; starting there now would be speculative.

`CreateSessionCommand` currently checks the calling server's
`WhitelistEnabled` then looks up an approved application for
`(accountId, serverClientId)`; it now checks the hive setting and looks up
by `accountId` alone.

Two incidental cleanups fall out: `AccountEndpoints` and `WhitelistEndpoints`
read the claim with `?? string.Empty` (fail-open, inconsistent with
`ICurrentGameServer`'s fail-closed throw) — that inconsistency disappears
along with the field. The whitelist review queue is already cross-server and
needs no change.

---

## Part 3 — Character: durable facts vs. volatile presence

Location is written by a periodic heartbeat (~30–60s per connected player).
That frequency is the load-bearing constraint: appending an event per
heartbeat would bloat the event store without producing anything anyone
wants to replay. So character state splits along a line that is worth
stating as a rule, because it will keep coming up:

> **Durable, auditable facts are events. Volatile current state is a
> document.**

### `Character` (event-sourced, mechanism unchanged)

- Gains `CurrentServerId: GameServerId`, carried on `CharacterCreated` and
  changed only by travel — a rare, genuinely auditable transition.
- **`Cash` is deleted.** It is declared on the aggregate, set by no event,
  read by no code; its only other reference is `Assert.Equal(0m, character.Cash)`
  in a unit test. `MIGRATION.md §6` confirms Banking superseded it. Removing
  it now is what makes the `TravelState` decision below consistent rather
  than arbitrary.

### `CharacterPresence` (new — a plain Marten document, **not** a projection)

```csharp
public sealed class CharacterPresence
{
    public required CharacterId CharacterId { get; init; }   // identity
    public required GameServerId ServerId { get; set; }
    public required CharacterTransform Transform { get; set; }
    public required DateTimeOffset LastHeartbeatAt { get; set; }
}
```

`CharacterTransform` carries position, full rotation, velocity, and stance —
enough to restore a reconnecting player exactly as they left.

Registered as an ordinary document on the Characters store, explicitly not
via `options.Projections.Add(...)`, so it never enters the event stream.
This is the one place where a reviewer might "helpfully" make it consistent
with the module's two inline projections; the comment on the registration
should say why it must not be.

### Heartbeat endpoint

`PUT /api/characters/{characterId}/presence`, reusing the existing
`gameserver:characters:write` scope (this is character state written by a
gameserver — it needs no new scope, and adding one means realm config churn
for no authorization benefit).

**A heartbeat whose calling server is not the character's `CurrentServerId`
is rejected.** This is the invariant that keeps a character from appearing
live on two maps at once. It is the reason to build presence now rather than
alongside travel: travel is only safe if this rule already holds.

### Coordinates are map-local

A transform is meaningful only on the server that recorded it. It restores a
player reconnecting to the **same** map. It is explicitly **not** carried
across a travel handoff — a character arriving on another map spawns at that
map's arrival point with velocity zeroed. Arrival-point configuration on
`GameServer` is travel scope.

### What this unlocks

`LastHeartbeatAt` is the signal the stale-session problem has always lacked.
`Character.StartSession()`'s comment and `docs/characters.md` both note that
an ungraceful gameserver crash leaves `SessionActive = true` with nothing to
end it, and name the unbuilt fix as reconciling by gameserver instance
identity. A session that has stopped reporting is now detectably dead.
Reaping is not built here; the data now exists.

---

## Part 4 — Placement as an attribute

`Shop` gains `ServerId: GameServerId`. A shop is a building on a map. Its
`OwnerCharacterId`/`OwnerCompanyId` and `PayoutBankAccountId` remain
hive-wide — which is the point of the whole change: a shop on Map A owned by
a character currently on Map B, paying into a hive-wide bank account, is now
an ordinary set of references rather than something tenancy forbids.

Nothing else gets a `ServerId`. `Bank` branches, world-placed item
instances, and company premises are YAGNI until a feature needs them.

---

## Deferred

- **`Character.TravelState`.** The state machine is
  `Settled | InTransit(From: GameServerId, To: GameServerId, StartedAt)`.
  Invariants: an in-transit character has no active session on either
  server; only the destination may complete arrival; arrival sets
  `CurrentServerId` to the destination and discards the prior transform.
  **The field is not added in this change.** Nothing could transition it, so
  it would be dead state — precisely the `Cash` mistake this spec deletes.
- **Travel orchestration**: depart/arrive events and endpoints, Bridge
  coordination, `GameServer` arrival points, crash-mid-travel recovery.
- **Concurrency control on the character stream.** `StartCharacterSessionHandler`
  does `LoadAsync` → mutate → *unversioned* `Append`, with no
  `FetchForUpdateAsync` equivalent anywhere in Characters — two callers
  racing the same character both succeed. Tolerable today. **Travel cannot
  ship without fixing this**, because a race there duplicates a character
  across maps. Note that adding versioned appends interacts with the inline
  projection double-apply footgun documented in `MartenBankAccountRepository`
  — `Character.StartSession()` self-applies.
- **Stale-session reaping** from `LastHeartbeatAt`.

## Error handling

- Heartbeat from the wrong server: `409 Conflict` `ProblemDetails` — the
  character exists but is not on the calling server. Distinguishable from
  `404`, deliberately: unlike the superseded spec's trust boundary, there is
  no longer a reason to hide the difference between "not here" and "doesn't
  exist" within a hive.
- Heartbeat for an unknown character: `404`, matching
  `StartCharacterSessionResult.CharacterNotFound`.
- Unknown `client_id` at registry resolution: the existing fail-closed throw.
  It still surfaces as an unhandled 500 — mapping `ICurrentGameServer`'s
  throw to `ProblemDetails` was deferred by the superseded spec and stays
  deferred, but it is now reachable via a second path (an unregistered
  server, not just a missing claim), which makes it more likely to be hit.

## Testing

- **Inverted tenancy tests** (all five modules): data created via one
  gameserver client is now visible from another.
- **`Characters.IntegrationTests`**: heartbeat from the character's current
  server succeeds and upserts; heartbeat from a different server returns
  `409`; heartbeat for an unknown character returns `404`.
- **Event-store bloat guard**: assert a character's event count is unchanged
  across N heartbeats. This is the regression test for the durable-vs-volatile
  rule — without it, someone converting presence to a projection would break
  the design silently and no other test would notice.
- **`EXPLAIN`** each of the three `FOR UPDATE` locks post-change, confirming
  an index scan.
- **Cross-module atomicity unchanged**: `PurchaseCompanySharesCommand` and
  `PurchaseListingCommand` still commit atomically.
- **Registry**: create + list; an unknown `client_id` no longer silently
  yields a default server.

## Decisions log

- Conjoined tenancy is **removed**, not re-keyed to a hive id. One
  deployment is one hive, so a hive-keyed tenant would have exactly one
  value — machinery with a real cost (composite PK, row-lock SQL, per-module
  ports) and no partitioning to do. Revisit only if multiple hives per
  deployment becomes a goal.
- Everything is hive-wide, including the economy: one balance and one set of
  companies usable from any map. Per-map economies were considered and
  rejected — they would make travel punishing rather than a feature.
- Whitelist is per-hive: approved once, play anywhere. Per-map gating is
  given up deliberately; if a single restricted map is ever needed it comes
  back as an explicit feature, not as a side effect of tenancy.
- Server identity is promoted to a `GameServerId` GUID now. The superseded
  spec deferred this on the reasoning that Marten needed a string tenant key
  anyway; that reasoning does not survive server identity becoming durable
  domain data.
- Presence is a mutable document, not events — heartbeat frequency makes the
  event store the wrong home. Stated as a general rule (durable facts are
  events, volatile state is a document) because it will recur.
- The wrong-server heartbeat rejection ships in this change rather than with
  travel: it is the invariant travel depends on, and it is cheap to enforce
  before there is any travel to get wrong.
- `TravelState` is specified but not added, and `Cash` is deleted, for the
  same reason: a field nothing can transition is dead state.
- Transforms are map-local and discarded on travel, rather than translated
  between maps.
