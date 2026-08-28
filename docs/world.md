# World

Persists what a character carries and what the world looks like across gameserver restarts. This doc
covers the grant path — shop purchases and gathering minting items — the delivery loop that hands them
to the game, and the [snapshot write path](#the-snapshot-write-path) the mod uses to report what the
game did on its own — including `Full`-mode reconcile (deleting the rows a snapshot leaves out) and
batch-level idempotency, both of which now ship. World-structure state is a later phase.

The client-facing contract for that write path — the wire shapes, every error code and its `retryable`
flag, the ordering and `sequence` rules, and what the Bridge is required to do in return — lives in
[Bridge](./bridge.md). This document is the *why*; that one is what you build a mod against.

## An instance is one entity

Reforger has no item stacking. Every `ItemInstance` row is one discrete entity — buying ten bandages
mints ten rows, not one row with a quantity of ten. This isn't a modelling choice made for
convenience, it's what the engine actually does: `InventoryItemComponent` has no quantity/count/stack
property, and storage is slot-based with nested parent/child containers. A magazine is the case that
makes this concrete — it's one instance carrying an integer `ammo` count (`BaseMagazineComponent`'s
`GetAmmoCount()`/`SetAmmoCount()`), never a container of individual rounds. Nothing here splits and
nothing merges, which is also why the backend never needs to reconcile a "stack" across two API
responses.

## The backend is the sole minter of `ItemInstanceId`

Every instance is born through a backend API call — a shop purchase, a gathering action, a staff
grant. The mod never mints an id of its own. An id the backend never issued is always rejected, with
no exception: there is no legitimate way for an unknown id to show up, since nothing splits or merges
on the game side for one to be minted from. This is what makes ack "adopt this id" rather than "tell
me what you called it" — see [Acking](#acking) below, and
[Bridge](./bridge.md#the-adoption-rule-the-single-most-important-rule-here) for the ordering a mod has
to get right when it adopts one.

## Scopes

| Scope | Grants |
|---|---|
| `gameserver:inventory:read` | `GET /api/inventory/limits`, `.../characters/{id}/items`, `.../characters/{id}/pending` |
| `gameserver:inventory:write` | `POST /api/inventory/acks`, `.../instances/{id}/spawn-failed`, `POST /api/inventory/snapshots`, `POST /api/gathering/actions`, `POST /api/inventory/unknown-prefabs` |
| `inventory:manage` | staff: `GET /api/inventory/undeliverable`, `GET /api/inventory/unknown-prefabs`, `PATCH /api/inventory/limits` today; movement audit, item removal and prune land with later phases |

Both `gameserver:*` scopes are granted to `gameserver-dev`. `inventory:manage` is **not** — same
reasoning as `accounts:manage` being withheld from the gameserver client (see
[Accounts](./accounts.md)): it's a staff action, not something the game server does on its own. Get a
staff token from `staff-admin-dev` the same way [Accounts](./accounts.md#locking-and-unlocking-an-account)
does.

## Walkthrough

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)), a registered gameserver (see
[Game server registry](./accounts.md#game-server-registry)), a `characterId` from
[Characters](./characters.md), a bank account from [Banking](./banking.md), an `itemId` from
[Items](./items.md), and a shop listing from [Shops](./shops.md). This walkthrough buys from a shop,
watches the purchase's `grantedInstances` become pending deliveries, acks them into held items, then
gathers a second batch directly.

```sh
ITEM_ID=$(curl -s -X POST http://localhost:5100/api/items \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"displayName":"Bandage","prefabClassName":"Medical_Bandage"}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['itemId'])")

# The shop's owner and the buyer are different characters/bank accounts on purpose — Banking rejects
# a transfer where the source and destination account are the same.
BANK_ID=$(curl -s -X POST http://localhost:5100/api/banks \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"First E-Life Bank","transactionFeeBase":0,"transactionFeeMultiplier":0}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['bankId'])")

PAYOUT_BANK_ACCOUNT_ID=$(curl -s -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['bankAccountId'])")

SHOP_ID=$(curl -s -X POST http://localhost:5100/api/shops \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"ownerType\":\"Personal\",\"ownerCharacterId\":\"$CHARACTER_ID\",\"displayName\":\"Joe's Guns\",\"payoutBankAccountId\":\"$PAYOUT_BANK_ACCOUNT_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['shopId'])")

LISTING_ID=$(curl -s -X POST http://localhost:5100/api/shops/$SHOP_ID/listings \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"itemId\":\"$ITEM_ID\",\"price\":5,\"stock\":10,\"actingCharacterId\":\"$CHARACTER_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['listingId'])")

BUYER_BANK_ACCOUNT_ID=$(curl -s -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$BUYER_CHARACTER_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['bankAccountId'])")

curl -s -X PUT http://localhost:5100/api/bank-accounts/$BUYER_BANK_ACCOUNT_ID/deposit \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"amount":1000}' > /dev/null
```

### Purchase, then watch it arrive

```sh
curl -s -X POST http://localhost:5100/api/shops/$SHOP_ID/listings/$LISTING_ID/purchase \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"quantity\":3,\"buyerCharacterId\":\"$BUYER_CHARACTER_ID\",\"buyerBankAccountId\":\"$BUYER_BANK_ACCOUNT_ID\"}"
```

The response carries `grantedInstances`, one entry per unit — three bandages purchased means three
freshly minted instance ids, each with `PendingSpawn = true` on the row behind it:

```jsonc
{
  "totalPaid": 15, "newStock": 7,
  "grantedInstances": [
    { "instanceId": "…", "itemId": "…", "prefabClassName": "Medical_Bandage" },
    { "instanceId": "…", "itemId": "…", "prefabClassName": "Medical_Bandage" },
    { "instanceId": "…", "itemId": "…", "prefabClassName": "Medical_Bandage" }
  ]
}
```

The response is a convenience, not the source of truth — losing it loses nothing, because the same
three rows show up here regardless:

```sh
curl -s http://localhost:5100/api/inventory/characters/$BUYER_CHARACTER_ID/pending \
  -H "Authorization: Bearer $BRIDGE_TOKEN"
```

Each row comes back with `"pendingSpawn": true` and `"origin": "ShopPurchase"` — a purchase, gather,
or staff grant that has never been spawned in-game, oldest first, bounded by
`WorldSettings.MaxPendingPageSize`. This is what a portal purchase looks like at every join until the
mod delivers it: there's no game session at the moment of payment, so `pending` is the only place the
item exists until the next time the character connects.

### Acking

The mod spawns what it can and acks — batched, one call for the whole delivery, not one call per
item:

```sh
curl -s -X POST http://localhost:5100/api/inventory/acks \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"acks":[
        {"instanceId":"<first granted instanceId>","children":[]},
        {"instanceId":"<second granted instanceId>","children":[]},
        {"instanceId":"<third granted instanceId>","children":[]}
      ]}'
```

```jsonc
[
  { "instanceId": "…", "outcome": "Cleared", "children": [] },
  { "instanceId": "…", "outcome": "Cleared", "children": [] },
  { "instanceId": "…", "outcome": "Cleared", "children": [] }
]
```

The ack is bounded by **count**, not body size: more than `maxAcksPerBatch` entries, or more than
`maxChildrenPerAck` children on any single entry, is rejected whole as `batch_too_large` (`400`, not
retryable — chunk it and resend). Both caps come from `GET /api/inventory/limits`, so the Bridge
chunks correctly instead of discovering them as rejections.

**Correction worth flagging explicitly:** an earlier draft of the design described acking an unknown
id as a `404`. That isn't the shipped contract — `POST /api/inventory/acks` is **batched**, so the
whole call still returns `200`, and an unknown id just comes back with its own entry as
`"outcome": "NotFound"` alongside whatever else was in the same batch. Nothing about one bad id in a
batch of ten fails the other nine.

Now the same instances show up on the held-items read instead, with `pendingSpawn: false`:

```sh
curl -s http://localhost:5100/api/inventory/characters/$BUYER_CHARACTER_ID/items \
  -H "Authorization: Bearer $BRIDGE_TOKEN"
```

`GET .../items` and `GET .../pending` are always disjoint — a row appears on exactly one of them,
never both, and never neither (short of soft-delete/staff removal). Ack is also what an
**engine-spawned child** rides in on: an ack entry's `children: [{ itemId, slot }]` mints one instance
per declared child, parented to the acked instance — the only way a composed prefab (a magazine seated
in a rifle, a battery in a radio) gets an id at all, since the mod never mints one itself.

An ack arrives late by design — the Bridge is store-and-forward with retries — so it races the one
thing that can make its subject disappear: a snapshot reporting the item as consumed before the ack
ever landed. The delete wins, in both halves. The ack's own write is a patch, so it updates a row that
is gone rather than resurrecting it; and any children the same entry declared are discarded rather
than minted under a parent that no longer exists, with that entry reported `"outcome": "NotFound"` —
by the time the batch committed, the instance really was not there.

### Gathering

A gather action grants the item and its skill XP in one commit — the two can never diverge, so there
is no way to end up with XP but no loot or loot but no XP:

```sh
curl -s -X POST http://localhost:5100/api/gathering/actions \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$BUYER_CHARACTER_ID\",\"action\":\"MinedOreDeposit\",\"itemId\":\"$ITEM_ID\",\"quantity\":2}"
```

```jsonc
{
  "gains": [ { "skill": "Mining", "xpGained": 50, "newTotalXp": 50, "newLevel": 1, "didLevelUp": false } ],
  "grantedInstances": [
    { "instanceId": "…", "itemId": "…", "prefabClassName": "Medical_Bandage" },
    { "instanceId": "…", "itemId": "…", "prefabClassName": "Medical_Bandage" }
  ]
}
```

Same `grantedInstances` shape a purchase returns and the same pending→ack path — the mod's
adopt-and-ack logic is written once and works unmodified against either producer.

Gathering is an inventory write, so it is **server-guarded** exactly like acking: a gather for a
character whose `CurrentServerId` is a different gameserver is `409`, and mints neither the items nor
the XP. (A purchase carries no such guard on purpose — a portal buy has no gameserver at all, and the
delivery server is resolved later, at ack time.) `quantity` must be greater than zero and no more than
`maxInstancesPerGrant`; both are checked before anything is written, so there is no way to record a
gather that granted nothing.

## The delivery loop

**The delivery cap.** Every time a row is served by `GET .../pending`, its `deliveryAttempts` counter
goes up — being handed to the mod in that payload *is* what counts as an attempt, whether or not the
mod actually manages to spawn it. Once `deliveryAttempts` reaches `WorldSettings.MaxDeliveryAttempts`
(3 by default, see `GET /api/inventory/limits`), the row stops being offered at all.

**A negative ack** (`POST /api/inventory/instances/{id}/spawn-failed`, reason one of `InventoryFull`,
`PrefabMissing`, `ContainerMissing`, `AdoptionUnsupported`) is how "it didn't fit" turns into a retry
instead of a silent drop. This matters most for a portal purchase: ten items can be delivered into an
inventory with room for three, with no pre-flight check possible, and a mod that just drops the
overflow would leave it pending forever — re-spawning at every future login. The response is
`StillPending` (try again next join) or `Undeliverable` (the cap was already hit).

**Undeliverable → a staff queue, never an automatic refund.** Once a row hits the delivery cap it
shows up in `GET /api/inventory/undeliverable` (needs `inventory:manage`) with its `origin`/
`originRef` intact, so a human can see exactly what was owed and to whom, and redeliver or refund by
hand. There's no automatic refund path — that would need a cross-module unwind touching Banking and a
policy for a partially-delivered multi-item order, and it becomes a griefing surface the moment a
player can make delivery fail on purpose.

**Unknown prefabs → the catalog's own growth signal.** A grant only ever mints an `ItemInstance` for a
prefab already in the catalog — an uncatalogued one is never persisted (see [Items](./items.md)).
`POST /api/inventory/unknown-prefabs` is how that gap stays visible instead of silent: the mod reports
`{ prefabClassName, count, firstSeenAt, sampleContext }` for whatever it saw with no catalog entry, and
the backend upserts one `UnknownPrefabSighting` per distinct name, keyed on a deterministic id derived
from the name, hive-wide across every gameserver — a repeat report increments `count` and advances
`lastSeenAt` rather than creating a second row. `GET /api/inventory/unknown-prefabs` (needs
`inventory:manage`) is the resulting staff promotion queue: sorted by `count` descending, filterable by
`minCount`/`since`, and paginated (`offset`/`limit`, unlike `GET /api/inventory/undeliverable` above)
so staff can work through it a page at a time.

## The snapshot write path

`POST /api/inventory/snapshots` is how the mod reports state the backend didn't cause — a player moved
a rifle into a backpack, dropped a crate, fired a magazine down to nine rounds. One batch carries an
`upserts` array and a `deletes` array, and the whole batch commits in a single transaction: all of it
lands or none of it does, which is what lets the Bridge retry a failed POST without reasoning about
partial application.

**Revision is the conflict resolution, not a lock.** Each instance carries a `revision` the mod bumps
on every change. A higher revision wins. A lower one is discarded and counted in `skippedNoOp` — the
backend already holds strictly newer content, so there is nothing to do. An *equal* revision carrying
different content is the interesting case: two writers disagree about the same instance at the same
point in its history, so it is rejected `IdentityConflict` rather than silently overwritten. Nothing
on this path takes a row lock; revision comparison plus the batch transaction is the whole mechanism.

**A snapshot never creates a row.** The [sole-minter rule](#the-backend-is-the-sole-minter-of-iteminstanceid)
applies here with no exception: an upsert naming an id no grant ever issued is rejected
`UnknownInstance` and creates nothing, whatever its parent kind. So is an upsert nesting into a
container the backend never issued. This is the strongest anti-duplication lever the design has —
because nothing in Reforger splits or stacks, there is no honest way for the mod to be holding an id
the backend hasn't seen.

**Reporting a pending instance counts as adopting it — but only from the right batch.** A row minted
by a grant is `pendingSpawn` until the mod acks it. If that ack is lost, the mod's next snapshot
naming the instance clears the flag anyway — the mod could not report an instance it had not spawned.
That path deliberately applies regardless of revision, because a backend-minted row starts at revision
0 and so does the mod's own counter; comparing them would discard the item's first real change and
strand the row in the pending queue forever. The mirror case is a delete: an item consumed before its
ack ever landed clears `pendingSpawn` as it is removed, so it is not re-offered at the player's next
login.

An undelivered grant has no `rootGameServerId` yet — that is what "undelivered" means — so the ordinary
server guard has nothing to check it against. A pending row is therefore only touchable from a
`Character`-scoped batch naming the character it is owed to, whose own presence on the calling server
the batch has already had to prove. Without that, any server holding the id could drop somebody else's
paid, undelivered item onto its own map's ground, where it would lose its owner and start despawning.

The same reasoning applies to whatever a snapshot nests an item *into*, and to a batch's own scope: a
container nobody has taken delivery of is not somewhere anything can be put, so both are rejected —
unless the same batch is adopting that container too, which is the honest case of a mod spawning a
granted crate and reporting its contents in one snapshot. And because clearing `pendingSpawn` makes a
row live, an applied row is never left without a `rootGameServerId`: a live row with no server root
would satisfy neither guard, which is the first hole reconstituted in two steps.

**Rejections are per instance, failures are per batch.** An uncatalogued `itemId`, an out-of-range
`revision`/`durability`/`ammo`, a container cycle or over-deep nesting, an oversized attribute bag, a
staff-removed row, an instance on another gameserver, an unknown id, an identity conflict, a stale
delete — each is reported in `rejected` and the rest of the batch still applies.

A batch-level failure is different in kind: nothing is written at all, and the response is a problem
document rather than a 200. Setting aside malformed requests, there are nine of them — a duplicate
`instanceId` within one batch, an over-sized array (chunk against `GET /api/inventory/limits`), a
`sequence` outside `0..10^15`, a `Full` scope that is not a single character or container, a scope not
reachable from the calling gameserver (409, and deliberately *not* naming which other gameserver holds
the character), a `Full` whose `sequence` has already been superseded (409, carrying
`lastAppliedSequence`), a `Full` refused by the empty-payload guard (422), two `Full` batches racing
one scope's cursor (409), and exceeding the endpoint's rate limit (429).

**Seven of the nine are `retryable: false`; two are `retryable: true`.** The two exceptions are the
ones that name no fault in the request — a lost cursor race and a rate limit — and both want a plain
unmodified resend. Every other one is a pure function of the request, so resending reproduces it
forever. [Bridge](./bridge.md#errors-per-batch) carries the full table with status codes and what a
client should do with each; that table, not this paragraph, is the contract.

**Moving a container moves what's inside it, and deleting one takes them with it.** `rootCharacterId`,
`rootGameServerId` and `expiresAt` are denormalised down the container chain, so dropping a crate on
the ground has to rewrite all three for everything nested inside — including the ground TTL, which is
the one that breaks quietly. `rootCharacterId` is the hot inventory read, so getting this wrong is not
cosmetic: a crate that changes hands with stale contents surfaces those contents in the *previous*
player's inventory. Deleting a container likewise soft-deletes its descendants, or a child ends up
pointing at a row that no longer exists while still answering that same read.

The mod is under no obligation to re-report the inside of a crate it merely moved, so neither of those
is reachable from the batch's own entries. The batch walks the chain itself — upward before the diff
so the cycle guard sees whole chains, downward afterwards to reach subtrees — a level at a time, each
level one batched query, capped at the container depth limit. That is bounded by nesting depth, never
by how many entries the batch carries.

Cascaded deletes are reported in the response's `cascadeDeleted`, kept separate from `deleted` so that
`deleted` still counts exactly the deletes the caller asked for and its own arithmetic closes, while
the caller is still told how many rows actually went away.

The upsert side of that arithmetic has one term the delete side does not, and
[the Bridge contract](./bridge.md#response-200) states it as a caveat on the identity rather than
leaving a client to discover it: an upsert whose row the *same* batch cascaded out of existence — it
was moved into a container the batch also deleted — is counted in neither `applied` nor `rejected`.
`applied` deliberately counts writes that survived the batch (`appliedInstanceIds.ExceptWith(removedInstanceIds)`
in `ApplySnapshotHandler`), because nothing the upsert said survives; and nothing about the entry was
wrong, so it is not a rejection either. The row is still counted, in `cascadeDeleted`.

**Every write on this path — and on the ack path — is a targeted patch, never a document replacement.** `ItemInstance` has no
optimistic concurrency, so writing a whole document back writes every field of whatever copy the batch
happened to load. That resurrects a `pendingSpawn` flag another writer cleared — the phase 1 review's
duplicated paid item — and, confirmed by an integration test against real Postgres rather than
reasoned about, it also *undeletes* a row a concurrent batch soft-deleted, bringing it back still
pending so the delivery loop serves a consumed item a second time. A patch is an `UPDATE`: against
that same row it matches nothing and writes nothing, which is the correct outcome rather than merely a
safer one, and it cannot insert, which means the sole-minter rule is enforced by the storage operation
itself and not only by the check in front of it. The two patches differ only in width — an applied
upsert writes the whole surface a snapshot owns, a descendant writes just the three derived root
fields — and neither names `origin`, `originRef` or `registeredAt`, so the persistence layer
independently cannot overwrite what the domain already makes unassignable.

[Acking](#acking) is written the same way, over the three fields it owns, and it is if anything the
more urgent of the two: an ack arrives late by design, so its window is the widest in the module. The
case that window costs is a granted item consumed before its ack ever landed — the snapshot delete
correctly removes it, and a whole-document ack write would then put an already-used item back into
live inventory.

## Tuning the limits

`GET /api/inventory/limits` composes three things: the fifteen `WorldSettings` knobs, the structural
domain constants (container depth, the three attribute caps, the `Full`-mode `sequence` ceiling), and
the two rate-limit buckets read back off the same options objects the limiter itself is built from.

Only the first of the three is writable, through `PATCH /api/inventory/limits` (`inventory:manage`).
It is a partial update — omitted fields keep their stored value — and every supplied value is
**range-checked, not clamped**: an out-of-range knob comes back `setting_out_of_range` (`400`,
`retryable: false`) naming the knob and its allowed range, and nothing is written, so a request naming
one good value and one bad one is never half-applied. The ranges live in one table in
`UpdateWorldSettingsHandler`, with the two rules that set them written above it.

The structural constants are deliberately not settable: they are invariants already baked into stored
rows, so a runtime edit would retroactively invalidate data that was valid when written. The
rate-limit figures are not settable either, for the opposite reason — they are derived from what is
enforced, and a separately-stored copy could only ever drift from it.

This is newer than the settings themselves. Phase 2 accepted three reconcile-guard thresholds on the
grounds that they were retunable against real data while the repository exposed no write path at all
and the singleton table held zero rows, which the whole-branch review caught. `WorldSettingsTests` now
fails if a knob is added without being both publishable and settable, so the gap cannot reopen
quietly.

## An OpenAPI schema-collapse hazard

`grantedInstances` is field-for-field identical between `Shops.Api`'s `PurchaseListingResultDto` and
`World.Api`'s `GatherActionResultDto` on purpose — a `GrantedInstanceDto` is deliberately defined
separately in each module's own `*.Api` project (per [ARCHITECTURE.md §9e](../ARCHITECTURE.md#9e-modulith-structure--module-boundaries),
DTOs live beside their endpoint, and there's no shared DTO project). Same story for
`Characters.Api`/`World.Api`'s `SkillXpGrantDto`, used by `RecordSkillActionCommand` and
`GatherActionResultDto.Gains` respectively.

The OpenAPI generator (`Microsoft.Extensions.ApiDescription.Server`, feeding `openapi/eliferpg-api-v1.json`)
keys schema components on **short type name**, not full namespace, and has silently folded each pair
into one shared `GrantedInstanceDto`/`SkillXpGrantDto` schema in the generated spec. That's harmless
today only because both sides of each pair are structurally identical — the moment one side gains or
changes a field, the generator has to either rename a schema (a breaking change for the
Kiota-generated Bridge client, which names its model classes off that schema name) or emit one wrong
shared shape for whichever endpoint didn't ask for the change. Whoever next touches either DTO pair
should check the regenerated `openapi/eliferpg-api-v1.json` for exactly this before assuming "I only
changed one module's DTO."

## Related reading

- [Bridge](./bridge.md) — the client contract for everything above: the snapshot wire shapes, the
  rejection/error tables with their `retryable` flags, the adoption rule, the rate limits, and the
  buffering the Bridge is required to provide.
- [ARCHITECTURE.md §9e](../ARCHITECTURE.md#9e-modulith-structure--module-boundaries) — why
  `ItemInstance` is a plain document rather than an event-sourced aggregate, and a Marten gotcha
  (`Store()`+`Delete()` on the same id in one `SaveChangesAsync`) discovered while building the
  delivery loop.
- [Shops](./shops.md) and [Items](./items.md) — the catalog and the purchase path that feeds this
  module.
- [Skills](./skills.md) — the XP side of a gathering action.

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, which is on the Compose network — see the main [README](../README.md).)
