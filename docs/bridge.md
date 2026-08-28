# Bridge

The contract the Reforger mod and the Bridge Service are built against. Everything here is
implementable without reading this repository's source: the wire shapes, the rules that decide whether
a request is accepted, every rejection reason and error code, and what the backend guarantees in
return.

The Bridge itself lives in its own repository, `eliferpg-reforger-bridge` — it is an ASP.NET Core
minimal-API app listening on `:5200` on the gameserver host, not a module in this repo. This document
is the Central API's half of the contract; where it says the Bridge **must** do something, that is a
requirement on that repo, not a description of code that already exists.

- [The three hops](#the-three-hops), and the `Content-Type` the engine forces
- [What the Bridge must do, and what the backend guarantees](#what-the-bridge-must-do-and-what-the-backend-guarantees)
- [Identity and scopes](#identity-and-scopes)
- [The adoption rule](#the-adoption-rule-the-single-most-important-rule-here) — the single most important rule here
- [Limits: hardcode nothing](#limits-hardcode-nothing)
- [The snapshot wire contract](#the-snapshot-wire-contract)
- [Ordering, idempotency and replay](#ordering-idempotency-and-replay)
- [`Full` mode: what "everything" means](#full-mode-what-everything-means)
- [Rejection reasons](#rejection-reasons-per-instance-never-fatal) (per instance) and [errors](#errors-per-batch) (per batch) — including [what is *not* a problem document](#errors-per-batch)
- The other write endpoints: [acks and their `children`](#post-apiinventoryacks--batched-per-entry), [`spawn-failed`](#post-apiinventoryinstancesinstanceidspawn-failed--single-instance), [unknown prefabs](#unknown-prefab-reports)
- [Rate limits](#rate-limits), [how to size a flush interval](#sizing-your-flush-intervals), and [why the published number is a floor](#the-limit-is-per-api-instance-not-per-deployment)
- ["Refused to persist" never means "delete the entity"](#refused-to-persist-never-means-delete-the-entity)
- [Walkthrough](#walkthrough)

For *why* the inventory model looks like this — the grant path, the delivery loop, the undeliverable
queue, the catalog — read [World](./world.md). This document does not repeat it.

## The three hops

```
Enfusion script (server-side mod)  ──►  Bridge (localhost:5200)  ──►  Central API (:5100)
        no credentials                  holds the OAuth client         validates, persists
        no retries, no buffer           buffers, retries, batches
```

The mod never talks to the Central API. It has no credentials, no durable storage, and no way to
survive its own process restarting — all three of which the write path needs.

**The engine forces `Content-Type: application/x-www-form-urlencoded` on outbound requests.** Enfusion's
`RestContext` sets it regardless of the body actually being JSON, and it cannot be overridden from
script. This is absorbed at the Bridge's **local** listener: that listener accepts the engine's
mislabelled body, parses it as JSON anyway, and re-issues a properly-typed `application/json` request
to the Central API. The Central API sees normal JSON on every endpoint and **must never grow a form
binder** — doing so would push an engine quirk across a trust boundary and into the contract every
other client (Admin UI, NPC module, tests) shares.

## What the Bridge must do, and what the backend guarantees

### The Bridge must buffer and replay

The Central API is reachable over the public internet from a gameserver host. It will be unreachable
sometimes: a deploy, a network blip, a restart. Every write on this path describes something that
already happened in the game — a player already moved the rifle, already fired the magazine, already
dropped the crate — so a request that fails is not an action that can be re-attempted later. It is a
fact that will be lost.

So the Bridge **must**:

1. **Write every outbound batch to durable local storage before attempting to send it.** Not an
   in-memory queue: the process will be killed with the gameserver, and an in-memory queue loses
   exactly the backlog that a crash makes most valuable.
2. **Keep the batch until the Central API returns a definitive answer** — a `2xx`, or a `4xx`/`5xx`
   carrying `retryable: false`. Anything else (a timeout, a connection failure, a `retryable: true`
   response) means resend later, unchanged.
3. **Resend a retry byte-for-byte, under the same `batchId`.** A regenerated `batchId` is a new batch
   as far as the backend is concerned, and forfeits replay protection.
4. **Drop a batch the backend refused with `retryable: false`.** Those refusals are pure functions of
   the request; resending reproduces them forever. Log it and move on.
5. **Obey `Retry-After`.** It appears on every `429` and it is a real number, not a hint.
6. **Bound the buffer.** A gameserver disconnected for a week must not fill its disk. Age out oldest
   first — the newest batch describes state closest to the truth.

**This does not exist today.** The Bridge as it stands has two `AddStandardResilienceHandler()` calls on
its outbound `HttpClient`s, which is per-call retry and circuit breaking within the lifetime of one
request. That is not durable store-and-forward: it survives a flaky connection and loses everything to
a process restart. Older revisions of `ARCHITECTURE.md` described SQLite buffering as though it
shipped; it did not, and §3.2 now says "must" rather than "does". Treat this section as the
specification for that work.

### What the backend guarantees in return

Buffering is only safe if replaying a buffered batch is safe. It is, in three layers:

| Guarantee | Scope | What it means for the Bridge |
|---|---|---|
| **Batch idempotency** | `POST /api/inventory/snapshots`, keyed on `{gameServerId}:{batchId}`, within `batchIdRetentionSeconds` (default 24h) | Resending an already-applied batch returns the **original response byte-for-byte**, with `replayOfPriorBatch: true`, and re-applies nothing. |
| **Per-instance last-write-wins** | every upsert, always | Outside the retention window a replay is applied fresh, and is a no-op anyway: a `revision` no higher than the stored one is discarded and counted in `skippedNoOp`. |
| **A `retryable` flag** | every problem response the World module returns | Present on every `4xx`/`422`/`429` from the inventory and gathering endpoints. `true` means "resend this unchanged"; `false` means "drop it". Absent means treat it as `false`. |

The idempotency key is the **pair**, never `batchId` alone. Server A's batch and server B's batch under
the same `batchId` are different rows: B cannot read A's stored response, and — the half that matters
more — B cannot overwrite it either. A replay from a different gameserver is not a replay.

## Identity and scopes

The Bridge authenticates with OAuth 2.0 Client Credentials, **one Keycloak client per gameserver
instance** (`ARCHITECTURE.md` §4.2), so a compromised host is revoked on its own. The `client_id` claim
on that token is what the Central API resolves the calling gameserver from, so the client must be
registered first — see [Game server registry](./accounts.md#game-server-registry). An unregistered
`client_id` cannot call any endpoint that resolves the current gameserver.

| Scope | Grants |
|---|---|
| `gameserver:inventory:read` | `GET /api/inventory/limits`, `.../characters/{id}/items`, `.../characters/{id}/pending` |
| `gameserver:inventory:write` | `POST /api/inventory/acks`, `.../instances/{id}/spawn-failed`, `POST /api/inventory/snapshots`, `POST /api/inventory/unknown-prefabs`, `POST /api/gathering/actions` |
| `inventory:manage` | staff only — never granted to a gameserver client |

For acting *on behalf of a player*, the Bridge exchanges its own token for a player-impersonating one
against Keycloak **directly**, never through the Central API — see
[ARCHITECTURE.md §4.3](../ARCHITECTURE.md#43-player-identity-token-exchange) for the verified mechanics
and why the Central API cannot do it for you.

## The adoption rule: the single most important rule here

> **The backend is the sole minter of `ItemInstanceId`. The mod adopts the id it is handed, seeding it
> into the entity's persistence component *before* registering the entity, and never invents one.**

Ordering is load-bearing and it is the one thing a naive implementation gets wrong. Consider the two
shapes:

```
WRONG                                    RIGHT
  entity = SpawnEntity(prefab)             entity = SpawnEntity(prefab)
  RegisterEntity(entity)   ← id is ""      comp.SetInstanceId(id)   ← before anyone can observe it
  comp.SetInstanceId(id)                   RegisterEntity(entity)
```

Register-then-fix leaves a window — however short — in which the entity is live and registered with no
id, or with a placeholder one. Anything that snapshots, saves, or walks the entity list in that window
emits a batch naming the wrong id. Under any race at all (a player interacting on the same frame, an
autosave tick, a second spawn) that batch is emitted, and every id in it is one the backend never
issued, so the whole payload is rejected `UnknownInstance` and the item is silently not persisted. Seed
first, register second, with no code in between.

### Why an unknown id is *always* rejected, with no exception

Reforger has no item stacking. `InventoryItemComponent` has no quantity/count/stack property; storage
is slot-based with nested parent/child containers, and a magazine is one entity carrying an integer
ammo count rather than a container of rounds. **Nothing splits and nothing merges.** So there is no
mechanism by which the mod could ever come to hold an entity that legitimately needs an id the backend
has not issued — no "the stack of 10 became 6 and 4, I need a second id" case exists, because no stack
exists.

That makes "an unknown id is always rejected" a total rule rather than a strict default, and it is the
design's strongest anti-duplication lever: a snapshot **can never create a row**, for any parent kind,
under any mode. Every instance is born through a backend call — a shop purchase, a gathering action, a
staff grant, or an ack declaring an engine-spawned child (a magazine seated in a rifle, a battery in a
phone). See [World](./world.md#the-backend-is-the-sole-minter-of-iteminstanceid).

### `instanceId` entropy: a full 122-bit UUIDv4, from a CSPRNG

Ids are minted by the backend, so this constrains the *Bridge*, wherever it generates a correlation id,
`batchId`, or anything else it hands to the backend as a unique value — and it constrains any future
mod-side id generation absolutely.

**Enfusion's `Math.RandomInt` is not a CSPRNG.** A GUID stitched together from two `int32` draws has
**64 bits** of entropy, not 122, and 64 bits *will* collide across a hive: the birthday bound puts a
50% chance of collision at roughly 5×10⁹ ids, and a busy hive generating ids per entity per session
reaches numbers where collisions stop being theoretical long before that. A collided `batchId` is an
accepted replay that silently discards a real batch. Use the platform's UUIDv4 (`Guid.NewGuid()` on the
Bridge side, which is `RandomNumberGenerator`-backed) and never hand-roll one out of integer draws.

## Limits: hardcode nothing

`GET /api/inventory/limits` publishes every cap and threshold the write path enforces — including the
ones added late (`batchIdRetentionSeconds`, the three `suspiciousReconcile*` thresholds, the
unknown-prefab bounds, `maxAttributeKeyLength`, `maxSequence`, and the rate-limit buckets). Read it at
Bridge startup and size every batch, every buffer window and every flush interval from it. Nothing on
this list should ever appear as a literal in Bridge or mod code.

That is now a mechanically enforced claim rather than a promise: a repo test walks the settings and the
structural constants and fails if either gains a cap this payload does not publish. It was added
because two had already gone missing (`maxAttributeKeyLength` and `maxSequence`, both enforced, neither
published), and both are in the list above as a result.

**These values can change without a redeploy.** The tunable half is editable at runtime by staff
(`PATCH /api/inventory/limits`), so treat what you read at startup as current-and-may-change rather
than fixed for the life of the process. Re-reading periodically, or on any `batch_too_large` or
`suspicious_reconcile` you did not expect, is cheap and correct; hardcoding a value you once read is
the same mistake as hardcoding a literal.

```jsonc
{
  "maxInstancesPerGrant": 100,          // most entities one grant call may mint
  "maxContainerDepth": 6,               // structural: nesting depth
  "maxAttributeKeys": 16,               // structural: attribute bag size
  "maxAttributeKeyLength": 64,          // structural: one attribute key
  "maxAttributeValueLength": 256,       // structural: one attribute value
  "groundItemTtlSeconds": 3600,         // how long a dropped item survives
  "maxPendingPageSize": 50,             // GET .../pending page size (limit is clamped DOWN to this)
  "maxDeliveryAttempts": 3,             // after this many, a grant is parked as undeliverable
  "maxAcksPerBatch": 100,               // POST /acks: entries
  "maxChildrenPerAck": 32,              // POST /acks: children on ONE entry
  "maxUpsertsPerBatch": 1000,           // POST /snapshots: upserts[] length
  "maxDeletesPerBatch": 1000,           // POST /snapshots: deletes[] length
  "batchIdRetentionSeconds": 86400,     // how long a batchId is remembered for replay
  "suspiciousReconcileScopeRowsThreshold": 25,     // the Full-mode data-loss guard: see below
  "suspiciousReconcileUpsertsThreshold": 3,
  "suspiciousReconcileSweptPercentThreshold": 90,
  "maxUnknownPrefabSightingsPerBatch": 1000,
  "maxPrefabClassNameLength": 256,
  "maxSampleContextLength": 512,
  "maxCountPerSighting": 1000000,
  "maxUnknownPrefabQueryPageSize": 100, // staff endpoint
  "maxUnknownPrefabQueryOffset": 100000,// staff endpoint
  "maxSequence": 1000000000000000,      // structural: Full-mode `sequence` ceiling
  "snapshotRequestBurst": 120,          // rate limit: POST /snapshots, requests in reserve
  "snapshotRequestsPerMinute": 600,     // rate limit: POST /snapshots, sustained
  "unknownPrefabRequestBurst": 20,      // rate limit: POST /unknown-prefabs, requests in reserve
  "unknownPrefabRequestsPerMinute": 2   // rate limit: POST /unknown-prefabs, sustained
}
```

Batch caps are enforced as a **count**, never a body size, and checked before a single row is read — so
chunking against these numbers is exact rather than approximate. An over-sized array is
`batch_too_large` (`400`, `retryable: false`), and the problem detail names *which* array
(`upserts`/`deletes`) so the Bridge knows which axis to split on.

## The snapshot wire contract

`POST /api/inventory/snapshots` reports state the backend did not cause: a player moved a rifle into a
backpack, dropped a crate, fired a magazine down to nine rounds. One batch carries `upserts` and
`deletes`, and **the whole batch commits in one transaction** — all of it lands or none of it does,
which is what makes an unconditional retry safe.

### Request

```jsonc
{
  "batchId": "8f14e45f-…",              // UUIDv4, required. Your idempotency key. Stable across retries.
  "scope": {                            // required. Exactly ONE anchor — see below.
    "kind": "Character",                //   "Character" | "Container", verbatim (case-insensitive)
    "characterId": "d36827d5-…",        //   required iff kind == "Character"; MUST be absent otherwise
    "containerInstanceId": null         //   required iff kind == "Container"; MUST be absent otherwise
  },
  "mode": "Partial",                    // "Partial" | "Full", verbatim
  "sequence": null,                     // required iff mode == "Full". A Partial batch has no cursor,
                                        //   so it neither needs nor uses one — send null.
  "upserts": [
    {
      "instanceId": "2ccbb3a8-…",       // an id the BACKEND issued. Never invented.
      "revision": 2,                    // your own per-instance counter. Non-negative. Higher wins.
      "itemId": "2ad5986a-…",           // catalog item id
      "parent": {                       // exactly one of three variants, discriminated by kind
        "kind": "Container",            //   "Character" | "Container" | "World"
        "characterId": null,            //   required iff kind == "Character"
        "containerInstanceId": "029c…", //   required iff kind == "Container"
        "slot": "0",                    //   meaningful only alongside characterId/containerInstanceId
        "transform": null               //   required iff kind == "World": { position:{x,y,z}, rotation:{x,y,z} }
      },
      "durability": 0.75,               // optional. A 0..1 fraction. NaN is rejected.
      "ammo": 9,                        // optional. Non-negative integer. One count on this row.
      "attributes": { "k": "v" }        // optional freeform bag. Bounded by maxAttributeKeys /
                                        //   maxAttributeValueLength. May be {} but not null,
                                        //   and no value may be null.
    }
  ],
  "deletes": [
    {
      "instanceId": "11cb246d-…",
      "revision": 3,                    // non-negative. A revision LOWER than stored is rejected
                                        //   StaleRevision — unlike a stale upsert, a stale delete
                                        //   is destructive, so it is refused rather than skipped.
      "reason": "Consumed"              // "Consumed" | "Destroyed" | "Despawned" | "Traded" | "Unknown"
    }
  ]
}
```

**A scope carries exactly one anchor.** A `Character`-scoped batch must not also send
`containerInstanceId`, and a `Container`-scoped batch must not also send `characterId`. A scope naming
two anchors says "this batch is about a character" and "this batch is about a container" at once; it is
malformed on its face and is rejected `400` (`retryable: false`) for either mode. Send only the
companion id the declared `kind` uses.

All enums cross the wire as **strings**, parsed case-insensitively. There is no
`JsonStringEnumConverter` configured, so an ordinal integer will not bind.

**`required` in the schema means "the key must be present", not "the value may not be null".** Explicit
JSON `null` for `scope`, `upserts`, `deletes`, an array element, `parent`, `attributes`, an attribute
*value*, or a transform's `position`/`rotation` is a `400` with a message naming the field — not a
silent default and not a 500.

### Response (`200`)

```jsonc
{
  "batchId": "8f14e45f-…",
  "sequence": 1787897653000,   // echoed for a Full batch; null for Partial
  "applied": 3,                // upserts actually written
  "skippedNoOp": 0,            // upserts discarded as a stale-or-identical revision
  "deleted": 0,                // deletes[] entries carried out
  "cascadeDeleted": 0,         // EXTRA rows soft-deleted as descendants of a deleted container
  "swept": 1,                  // rows soft-deleted purely for being ABSENT from a Full payload
  "rejected": [                // per-instance problems; the rest of the batch still applied
    { "instanceId": "30e640f3-…", "reason": "UnknownInstance" }
  ],
  "replayOfPriorBatch": false  // true when this is a replay of a stored batchId
}
```

The counts are split so that the arithmetic over your own request closes:

```
deleted +                                        (rejected entries that were deletes) == deletes.length
applied + skippedNoOp + (upserts you also deleted out from under) + (rejected upserts) == upserts.length
```

The delete line is exact, always. The upsert line has one term that is usually zero and that you have
to account for yourself:

> **An upsert whose row this same batch deleted is counted nowhere.** `applied` counts writes that
> *survived* the batch, and there is exactly one way for one not to: you upsert an item, and in the same
> batch you delete a container it ends up inside, so the cascade soft-deletes it. Nothing the upsert said
> survives, so counting it as `applied` would describe a write that is not there. It is not a rejection
> either — nothing was wrong with it — so it does not appear in `rejected` and `rejected` stays empty.
>
> A batch that deletes no container the same payload upserts into (which is nearly all of them, and every
> `Partial` batch that deletes nothing at all) never hits this, and the plain three-term identity holds.

`cascadeDeleted` and `swept` are rows you never named — a deleted container's contents, and a `Full`
payload's omissions — so they are reported separately rather than folded in. Both are always `0` for a
`Partial` batch that deletes nothing. Note that the row above is the same row counted in
`cascadeDeleted`: the two numbers are consistent, they just answer different questions.

### Revision is conflict resolution, not a lock

Bump `revision` on every change to an instance. A **higher** revision wins. A **lower** one is discarded
into `skippedNoOp` — the backend already holds strictly newer content. An **equal** revision carrying
*different* content means two writers disagree about the same instance at the same point in its
history, and is rejected `IdentityConflict` rather than silently overwritten. Nothing takes a row lock;
revision comparison plus the batch transaction is the whole mechanism.

One deliberate exception: **reporting a still-pending instance clears `pendingSpawn` regardless of
revision.** A backend-minted row starts at revision 0 and so does your counter, so comparing them would
discard the item's first real change and strand the row in the pending queue forever. This is how a
lost ack recovers — you could not have reported an instance you had not spawned. It applies only from a
`Character`-scoped batch naming the character that instance is owed to; see
[World](./world.md#the-snapshot-write-path) for why an undelivered grant has no server of its own to
check against.

## Ordering, idempotency and replay

### `batchId`

A UUIDv4 you generate, stable across every retry of the same content. Replaying an already-applied
batch within `batchIdRetentionSeconds` returns the **original response** with `replayOfPriorBatch: true`
and applies nothing.

Outside that window the `batchId` is no longer looked up, and what happens next depends on `mode`:

- **`Partial`** — the batch is applied fresh. Safe on its own merits, because per-instance revision LWW
  makes resending the same content a no-op; you just get real counts instead of the original ones.
- **`Full`** — you get `stale_sequence` (`409`, `retryable: false`). The scope's cursor advanced when
  the batch first landed, and a `Full` must name a `sequence` strictly greater than the last one
  applied, so the replay cannot clear the gate. Also safe, and arguably the better outcome: it is
  refused rather than re-swept. Drop it and reconcile fresh at a higher `sequence`.

Neither is a state you should be able to reach — `batchIdRetentionSeconds` defaults to 24 hours,
comfortably longer than any buffer you should be holding — but if you do, that is what you will see.

Keyed on `{gameServerId}:{batchId}`. A `batchId` recorded under a different gameserver is never returned
to you, and yours can never be overwritten by another server reusing the same id.

Only a batch that actually **applied** is recorded, so replaying a *rejected* batch recomputes the
answer rather than looking it up — which is exactly as correct, because nothing a rejection wrote can
change what the recomputation decides. Every batch-level rejection leaves your data untouched: no
instance is created, changed or deleted by one.

One of them is not silent, though. `suspicious_reconcile` writes a staff record and commits it before
returning (that is the whole point of the guard — see [the empty-payload guard](#the-empty-payload-guard)),
so replaying a batch that trips it records a second refusal. That is intended; it costs you nothing and
it is what tells staff the mod is still reporting the same thing.

### `sequence` — `Full` batches only

`sequence` gates `Full` reconciles through a **per-scope** cursor (`Character:{id}` or
`Container:{id}`), never a per-server one: one character's ordering problem must not be able to block an
unrelated container's. A `Full` batch must name a `sequence` **strictly greater** than the last one
applied for its scope, or it is `stale_sequence` (`409`, `retryable: false`, carrying
`lastAppliedSequence` so you can see how far behind you fell).

Three hard rules:

1. **It must be non-negative.**
2. **It is bounded at 10¹⁵.** A plain incrementing counter or a **millisecond** Unix epoch fits
   comfortably (10¹⁵ ms is roughly the year 33658). A **microsecond** epoch, a nanosecond epoch, or
   .NET's `DateTime.Ticks` (100-nanosecond units, ~6.4×10¹⁷ today) **does not**, and is rejected
   `sequence_out_of_range` (`400`, `retryable: false`) from the very first batch — loudly,
   immediately, and non-destructively, rather than silently later. Pick a millisecond-or-coarser
   scheme.
3. **It is monotonic and cannot be rewound.** There is no reset. A poisoned high value permanently
   denies every future `Full` reconcile of that scope, which is why the ceiling exists at all.

A `Partial` batch needs no `sequence` and has no cursor — it is ordered by each instance's own
`revision`.

Two `Full` batches racing the same scope's cursor produce `concurrent_reconcile` (`409`) for whichever
commits second. This is the **one retryable rejection** on the endpoint: nothing was wrong with the
batch, it simply lost a race. Resend it unmodified.

## `Full` mode: what "everything" means

`mode: Full` means **"this is everything in this scope."** Every live row the scope holds that the
payload does not mention is soft-deleted and counted in `swept`. That makes it the only destructive
thing a gameserver can do by accident, so the rules around it are worth reading twice.

### Scope: `Character` or `Container` only

A `Full` batch must name one bounded `Character` or `Container`. Anything else is
`unsupported_full_scope` (`400`, `retryable: false`). **There is no server-wide reconcile in this
phase** — it would be a whole-deployment wipe triggerable by widening one field, and it lands later as
an explicitly-authorised staff operation with a dry run.

### A `Full` must enumerate the contents of any container it mentions

This is the rule most likely to cost you data, and it cuts both ways:

- **Mention a container and you are claiming to know what is inside it.** Its contents that your
  payload omits *are* swept.
- **Do not mention a container and you are claiming nothing about its inside.** Its contents are left
  alone entirely — silence about a thing you never looked in is not evidence of absence.

"Mention" means the container's own `instanceId` appears in your `upserts` or `deletes`, or it is the
batch's own scope container. A bare reference as some *other* row's `parent` does not count.

The practical consequence: a `Full` batch that walks a character's inventory must walk **into** every
container it reports, all the way down, or it must not report those containers at all. Reporting the
backpack but not its contents deletes the contents. This was verified live — see the
[walkthrough](#step-6-a-full-that-mentions-a-container-but-not-its-contents).

The upside of the same rule is that under-reporting is safe by default: a mod that reports fewer
containers than exist leaves stale rows behind rather than deleting live ones, and the next honest
`Full` fixes them.

### What is never swept

1. **A still-`pendingSpawn` instance.** The game has not spawned it yet, so its absence from a report of
   what the game can see carries no information at all. This is what stops a `Full` destroying a paid,
   undelivered purchase.
2. **A staff-removed tombstone.** It is a live row on purpose, so a later upsert of that id finds it and
   is rejected `RemovedByStaff` instead of resurrecting anything.
3. **A row your batch moved *out* of the scope.** Report a backpack as dropped and its unreported
   contents go with it rather than being deleted underneath it.
4. **Contents of a container you did not mention** (above).
5. **Any container a surviving row is still nested inside**, walked to a fixed point up the chain. Report
   the magazine but forget the rifle, and the rifle survives — rather than being deleted and leaving the
   magazine parented to nothing.

**A rejected upsert does not confer authority over other rows.** Its id still counts as *named*, so the
row it names is protected from the sweep — you reported it as present, and the backend merely declined
to write what you said about it. But an entry the batch failed to write does **not** unlock its
container's contents for deletion. The sharp case is a staff-tombstoned crate: the upsert naming it is
rejected `RemovedByStaff`, the crate survives, and its children are *not* swept.

### The empty-payload guard

A server that booted with a failed mod load will happily report an empty world, and a soft delete is
the only undo this design has. So a `Full` batch is refused when **both** of these hold:

- **The gate:** its scope currently holds **more than** `suspiciousReconcileScopeRowsThreshold` (25)
  **sweep-eligible** rows, *and*
- **the evidence test:** the batch carries **fewer than** `suspiciousReconcileUpsertsThreshold` (3)
  upserts, **or** its sweep covers **at least** `suspiciousReconcileSweptPercentThreshold` (90) percent
  of those same eligible rows.

Both are counted over **sweep-eligible** rows only: live rows that are neither undelivered grants nor
staff-removed — the rows a sweep could actually touch. A character holding 30 undelivered purchases and
2 carried items has a scope of **2** for this purpose, not 32.

A refusal is whole: `suspicious_reconcile` (`422`, `retryable: false`), recorded for staff review, and
the scope's cursor is **not** advanced — so your corrected reconcile is still accepted at the same
`sequence`. Nothing is lost; the scope just stays stale until an honest snapshot arrives.

**"The player logged out naked" is deliberately not a trip.** A character who genuinely holds a handful
of things and now holds nothing produces a zero-upsert batch that sweeps 100% of its scope — both
evidence arms fire — and it must still work. The gate is the only thing that distinguishes it from the
failure this guard is for: a five-row scope never reaches the row threshold in the first place.

**All three numbers are deployment-tunable and published on `GET /api/inventory/limits`.** They are not
constants of the protocol. Read them; do not assume 25/3/90. The trade they encode is asymmetric on
purpose — a false refusal costs one batch and a stale scope, a false acceptance costs a player their
inventory — and the right setting depends on how much a character carries under a given server's
ruleset.

Design a `Full` batch to clear the guard by construction: report what is actually there. A reconcile
that legitimately empties a large inventory should carry the items that remain; one that has genuinely
lost its view of the world should not be sent at all.

## Rejection reasons (per instance, never fatal)

Reported in `rejected[]`. The rest of the batch still applies, and the request is still a `200`. Log
these; do **not** retry them.

| `reason` | Means |
|---|---|
| `UnknownItem` | The `itemId` has no catalog entry. See [Items](./items.md), and report it via [unknown prefabs](#unknown-prefab-reports). |
| `UnknownInstance` | The backend never issued this `instanceId`. Always a client bug — see [the adoption rule](#the-adoption-rule-the-single-most-important-rule-here). Also covers nesting into a container the backend never issued. |
| `StaleRevision` | A **delete** naming a revision lower than the stored row's. (A stale *upsert* is not a rejection — it lands in `skippedNoOp`.) |
| `IdentityConflict` | **Two independent producers, and only one of them is about revision.** (a) The stored row carries a *different `itemId`* than your upsert names — a backend-issued id has been reported as a different item, which is a UUIDv4 collision, an entity-id mix-up in the mod, or something worse. This is checked before revision is ever compared, so it is **revision-independent**: bumping `revision` and resending will be rejected identically, forever. Stop sending that `instanceId` and log it — it is the one rejection here worth an alert rather than a log line. (b) Equal `revision`, different content: two writers disagree at the same point in that instance's history. Resending unmodified is also pointless; the fix is to stop having two writers, or to let one of them win by advancing `revision`. |
| `CycleDetected` | The parent chain forms a cycle, or exceeds `maxContainerDepth`. |
| `AttributeLimit` | The attribute bag exceeds `maxAttributeKeys` or a value exceeds `maxAttributeValueLength`. |
| `NotOnThisServer` | **"Not yours to report from here."** The plain case is the one the name suggests: the instance's character (or, for a Container/World-parented row, the instance itself) is currently on a different gameserver, so your batch is not the authority on it. The commoner case in practice is narrower and worth knowing: the row is still **`pendingSpawn`** — an undelivered grant — and an undelivered grant has no gameserver of its own for that check to use, so the backend requires the batch to be `Character`-scoped **on the character the grant is owed to** before it will let you touch it. Same answer for an upsert, a delete, or a container you are nesting into. So: if you are adopting a grant, send it from a `Character`-scoped batch naming that character (which is what reporting that character's inventory already looks like) and it will apply. Otherwise the character is somebody else's to report — drop the entry, do not retry. |
| `RemovedByStaff` | The row is a staff tombstone. It stays refused; this is sticky by design. |
| `ValueOutOfRange` | A typed scalar is nonsense: negative `revision`, `durability` outside `0..1` (NaN included), negative `ammo`. Checked in memory, before any database round trip. |

These names are an **append-only** enum on the wire. A client may safely treat an unrecognised reason as
"rejected, do not retry".

## Errors (per batch)

Every one of these is a [RFC 9457 problem document](https://www.rfc-editor.org/rfc/rfc9457)
(`application/problem+json`) carrying a machine-readable prefix in `title` and a `retryable` boolean.
Nothing was written.

> **Not every 4xx on this endpoint is one of these.** Two classes of failure are rejected by ASP.NET's
> model binder *before* any of this module's code runs, and they do **not** come back as problem
> documents:
>
> - **A body that is not valid JSON, or a field of the wrong JSON type** (`"revision": "one"` where a
>   number is expected) → **`400`** with `Content-Type: text/plain`. The body varies by environment —
>   in Development it is the developer exception page, elsewhere it is effectively empty — but it is
>   never `application/problem+json` and never carries `retryable`.
> - **A missing or non-JSON `Content-Type`** → **`415 Unsupported Media Type`**, with no body at all.
>   This is what the Bridge would get if it forwarded the Reforger engine's forced
>   `application/x-www-form-urlencoded` header verbatim instead of
>   [absorbing it](#the-three-hops).
>
> So a client must not write one parser for every 4xx. **Check the response `Content-Type` first**, and
> treat anything that is not `application/problem+json` as a non-retryable client bug — which both of
> these are. Both are reachable from a realistic mod bug (a serialiser emitting a number as a string is
> the common one), so this is worth handling rather than asserting away.
>
> The cause is that the host does not call `AddProblemDetails()`, which would map these onto problem
> documents. Making it uniform is a host-wide change affecting every module and is recorded as a
> recommendation, not something this contract assumes.

| `title` prefix | Status | `retryable` | What to do |
|---|---|---|---|
| *(shape errors: `scope is required`, `mode must be one of…`, `upserts[].attributes must not be null`, …)* | `400` | `false` | Client bug. Drop and log. |
| `duplicate_instance_id` | `400` | `false` | The same id twice in one batch, across `upserts` and `deletes` combined. Likely entity cloning. Drop and log. |
| `batch_too_large` | `400` | `false` | Chunk against `GET /api/inventory/limits` and resend as several batches, each with a **new** `batchId`. |
| `sequence_out_of_range` | `400` | `false` | Your sequencing scheme does not fit `0..10¹⁵`. Fix the scheme; see [`sequence`](#sequence--full-batches-only). |
| `unsupported_full_scope` | `400` | `false` | `mode: Full` needs one `Character` or `Container` scope. |
| `wrong_server` | `409` | `false` | The batch's scope is not reachable from the calling gameserver. Deliberately does **not** name which server holds it. Drop the batch — the character is somebody else's to report. |
| `stale_sequence` | `409` | `false` | A newer `Full` already landed for this scope. Carries `lastAppliedSequence`. Drop this batch and reconcile fresh at a higher sequence. |
| `concurrent_reconcile` | `409` | **`true`** | Two `Full` batches raced this scope's cursor and yours lost. **Resend unmodified.** |
| `suspicious_reconcile` | `422` | `false` | The [empty-payload guard](#the-empty-payload-guard). Recorded for staff; the cursor is not advanced. Do not resend — fix what the mod is reporting. |
| `rate_limited` | `429` | **`true`** | Wait for `Retry-After` seconds, then resend unmodified. |

`retryable: false` is on **every** problem the World module returns, including the ack, spawn-failed and
gathering endpoints — every one of those cases reproduces exactly on resend (an unknown instance, a
character on another server, a quantity out of range, an uncatalogued item). Only `429` and
`concurrent_reconcile` carry `true`. **Treat an absent `retryable` as `false`.**

### The other write endpoints

The rejection style differs by endpoint and it is worth knowing which is which:

#### `POST /api/inventory/acks` — batched, per-entry

Confirms that backend-granted instances were spawned, clearing `pendingSpawn` on each. **Unknown ids do
not fail the call**: the whole request still returns `200` and each entry carries its own outcome, so
one bad id in a batch of ten does not touch the other nine. The only whole-request failure is
`batch_too_large`.

```jsonc
{
  "acks": [                             // at most maxAcksPerBatch entries
    {
      "instanceId": "029c8e98-…",       // an id the backend granted
      "children": [                     // at most maxChildrenPerAck per entry. [] is normal.
        { "itemId": "5d11adc8-…", "slot": "magazine" }
      ]
    }
  ]
}
```

**`children` is how an engine-spawned sub-entity gets an id at all, and it is a core mod job.** A
composed prefab arrives with parts the backend never granted separately — a magazine seated in a rifle,
a battery in a radio. Declaring `{ itemId, slot }` mints one instance per child, parented to the acked
instance, and hands its id back. `itemId` is the catalog id of the child; `slot` is your own stable
name for the socket it occupies, and it is the key the mint is idempotent on — replaying the same ack
returns the same child ids rather than minting a second set. Since the mod never mints, this is the
*only* route by which a nested part becomes persistable.

```jsonc
[
  {
    "instanceId": "029c8e98-…",
    "outcome": "Cleared",               // Cleared | AlreadyCleared | NotFound | WrongServer | RemovedByStaff
    "children": [
      {
        "itemId": "5d11adc8-…",
        "slot": "magazine",
        "outcome": "Minted",            // Minted | ItemNotInCatalog | SlotItemMismatch
        "instanceId": "7f2a1c04-…",     // set only for Minted — adopt it, same rule as any other id
        "existingItemId": null          // set only for SlotItemMismatch
      }
    ]
  }
]
```

`children` on the response is populated for `Cleared` and `AlreadyCleared` and empty otherwise.
`SlotItemMismatch` means that slot was already minted for a *different* `itemId` than this ack
declared; the existing child's own `instanceId` is deliberately withheld rather than handed back under
the wrong `itemId`, and `existingItemId` tells you what is actually there.

#### `POST /api/inventory/instances/{instanceId}/spawn-failed` — single-instance

The negative ack: "this granted instance could not be spawned." Without it, an item that would not fit
is silently dropped and stays pending forever, re-offered at every future login.

```jsonc
{ "reason": "InventoryFull" }
```

`reason` is exactly one of, verbatim (case-insensitive):

| `reason` | Use it when |
|---|---|
| `InventoryFull` | No room on the character — the common case for a portal purchase delivered at join. |
| `PrefabMissing` | The catalog's `prefabClassName` does not resolve to a prefab this server has loaded. |
| `ContainerMissing` | The instance is parented to a container that is not present in-world. |
| `AdoptionUnsupported` | The entity type has no persistence component to seed an id into. |

Anything else is `400` (`retryable: false`) naming the four. Responses: `200` with
`{ "outcome": "StillPending" }` (it will be offered again next join) or `{ "outcome": "Undeliverable" }`
(the delivery cap was already reached — it is now a staff queue item); `404` for an unknown id; `409`
for wrong server, staff-removed, or an instance that is not pending. This call never changes
`pendingSpawn` or `deliveryAttempts` — both are owned by `GET .../pending` — it only tells you which
side of `maxDeliveryAttempts` the instance already landed on.

#### `POST /api/gathering/actions`

`200`, or `404`/`400`/`409`, all problem documents with `retryable: false`.

See [World](./world.md#the-delivery-loop) for what these outcomes mean and how the delivery loop uses
them.

### Unknown prefab reports

`POST /api/inventory/unknown-prefabs` is how the catalog's gaps stay visible instead of silent. Report
`{ prefabClassName, count, firstSeenAt, sampleContext }` for anything the mod saw with no catalog entry.
Rows are keyed on a deterministic id derived from the name, hive-wide, so a repeat report increments
`count` rather than creating a second row.

Every bound is checked before a single row is touched, and one bad entry fails the whole batch `400`
(`retryable: false`): `prefabClassName` non-empty and at most `maxPrefabClassNameLength`; `count` within
`1..maxCountPerSighting`; `firstSeenAt` no more than 5 minutes in the future (clock skew) and no more
than 30 days old; `sampleContext` at most `maxSampleContextLength` (empty is normalised to null).

This endpoint is **aggressively** rate-limited — 2 requests/minute sustained. Aggregate locally and
flush on a timer of **at least 60 seconds**; do not report per spawn. See
[Sizing your flush intervals](#sizing-your-flush-intervals) for why 60 and not 30.

## Rate limits

`POST /api/inventory/snapshots` and `POST /api/inventory/unknown-prefabs` are bounded by a **token
bucket partitioned on your `client_id`** — one bucket per gameserver, so another server's flood is not
your problem and yours is not theirs.

| Endpoint | Burst | Sustained |
|---|---|---|
| `POST /api/inventory/snapshots` | `snapshotRequestBurst` (120) | `snapshotRequestsPerMinute` (600) |
| `POST /api/inventory/unknown-prefabs` | `unknownPrefabRequestBurst` (20) | `unknownPrefabRequestsPerMinute` (2) |

Both figures are published on `GET /api/inventory/limits`; read them rather than assuming the defaults.
The burst is what a reconnecting Bridge may drain at once; the sustained rate is what it may hold
forever. **Nothing queues** — over the limit is an immediate rejection, never a slow success:

```
HTTP/1.1 429 Too Many Requests
Retry-After: 30
Content-Type: application/problem+json

{"title":"rate_limited: too many requests from this client — wait for the interval in the Retry-After header and resend unmodified","status":429,"retryable":true}
```

That is the whole contract: wait `Retry-After` seconds, resend unmodified, keep the `batchId`. A `429`
is never a reason to drop a batch.

### Sizing your flush intervals

**Unknown prefabs: flush no more often than once every 60 seconds.** The sustained rate is 2/minute and
the `Retry-After` on a rejection is 30 seconds, so a 30-second timer sits exactly on the replenishment
boundary with no room for clock drift, a retry, or a second gameserver process. 60 seconds gives you a
2× margin and still surfaces a new missing prefab within a minute of the first sighting — which is
plenty for something a human has to catalogue by hand. Aggregate sightings locally between flushes
(that is what `count` is for) rather than reporting per spawn; one batch may carry
`maxUnknownPrefabSightingsPerBatch` (1000) distinct names, so a single flush a minute is not a
constraint on how much you can report, only on how often.

**Snapshots: the limit is not the thing to design against — batch granularity is.** 600/minute against
`maxUpsertsPerBatch` of 1000 is 600,000 reported instances a minute from one gameserver. If you are
anywhere near it, you are sending one request per changed item; coalesce per-character batches instead.
Note that the burst does **not** let a backlog drain instantly: a Bridge flushing one batch per
character across 60 players produces ~60 requests/second, so a two-minute outage buffers ~7,000 batches
and pays them off at the sustained rate — roughly twelve minutes, not 12 seconds. The reserve buys an
unthrottled first few seconds of a reconnect, not an exemption from the sustained rate. Fewer, larger
batches shorten that recovery; nothing else you control does.

### The limit is per API instance, not per deployment

The limiter is **in-process**. If the Central API is ever run as more than one replica behind a load
balancer, each replica keeps its own bucket, so one gameserver's effective allowance is
*N × the published figure* — and which replica a given request lands on decides whether it is refused.
There is no shared store coordinating them.

That is a real caveat on a document whose premise is that you can trust
`GET /api/inventory/limits`. Read the published numbers as a **floor you are guaranteed**, never as a
ceiling you can calibrate against: a client that assumes exactly 600/minute and paces itself to 599
will behave correctly on one replica and simply under-use its allowance on several. What you must never
do is derive a pacing strategy from *observing* where 429s start, because that observation is a property
of the current replica count. Obey `Retry-After` and the published floor; nothing else is stable.

The Central API is a single deployable today (`ARCHITECTURE.md` §9a), so this is a forward-looking
caveat rather than a live one — but it is silent when it bites, which is why it is written down here
rather than discovered later.

### What is not limited

The other endpoints (`acks`, `spawn-failed`, `pending`, `items`, `limits`, `gathering/actions`) are not
rate-limited today. Do not read that as permission to poll them without bound.

## "Refused to persist" never means "delete the entity"

When the backend rejects an item — `UnknownItem` because the prefab is not catalogued, `UnknownInstance`
because no id was ever issued, an outright `400` on the whole batch — **the mod must leave the entity
in the world.**

The player is holding a real object. Deleting it because a bookkeeping call failed turns a backend
problem into a visible, unexplained loss of the player's property, and it does so at the exact moment
the backend is least able to tell you whether the loss was warranted. An uncatalogued item stays
in-world for the session; it simply does not survive a gameserver restart, which is a far smaller and
far more explicable failure.

Concretely:

- **Never** despawn, delete, or hide an entity in response to any API rejection.
- Report the prefab through [`POST /api/inventory/unknown-prefabs`](#unknown-prefab-reports) so staff
  can catalogue it and it starts persisting.
- If a *delivery* failed rather than a persist — the item would not fit, the prefab is missing, the
  container is gone — that is what
  [`POST /api/inventory/instances/{id}/spawn-failed`](./world.md#the-delivery-loop) is for. It turns
  "it didn't fit" into a retry at the next join rather than a silent drop, and after
  `maxDeliveryAttempts` it parks the row in a staff queue for a human to redeliver or refund by hand.

## Walkthrough

Every command below was run, in this order, against a local stack, and every response shown is the one
it produced. It is meant to be pasted a step at a time: each step is complete, and each variable is
either set here or carried from an earlier step.

Two things will not match literally on your run and are not meant to: **ids and timestamps are freshly
generated each time**, and the row order of the `held` helper is unspecified. Compare the *counts* and
the *shapes* — those are the contract.

Assumes `src/Api` is running and that you are calling from inside the devcontainer, which is on the
Compose network so `keycloak` resolves by hostname — see the main [README](../README.md). From the host,
substitute `http://localhost:8180` for the Keycloak URL.

### Step 0: tokens, a gameserver, a character, a catalog

```sh
API=http://localhost:5100
KC=http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token
jq() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

BRIDGE_TOKEN=$(curl -s -X POST $KC -d "grant_type=client_credentials" \
  -d "client_id=gameserver-dev" -d "client_secret=local-dev-only-not-a-real-secret" | jq "d['access_token']")
STAFF_TOKEN=$(curl -s -X POST $KC -d "grant_type=client_credentials" \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" | jq "d['access_token']")
SELF_TOKEN=$(curl -s -X POST $KC -d "grant_type=client_credentials" -d "scope=account:self:manage" \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" | jq "d['access_token']")

# Register the calling gameserver. Idempotent — re-registering updates in place.
curl -s -X POST $API/api/game-servers \
  -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" \
  -d '{"clientId":"gameserver-dev","displayName":"Server 1","mapName":"Everon"}' > /dev/null

# An account (see docs/accounts.md — identity is portal-first, so NOT session-bootstrap),
# then a fresh character to keep this walkthrough repeatable.
ACCOUNT_ID=$(curl -s -X POST $API/api/accounts/me -H "Authorization: Bearer $SELF_TOKEN" | jq "d['accountId']")
CHARACTER_ID=$(curl -s -X POST $API/api/characters \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"accountId\":\"$ACCOUNT_ID\",\"name\":\"Alice\"}" | jq "d['characterId']")

# Two catalog entries. POST is 409 if the prefabClassName already exists, so create-then-look-up
# keeps this re-runnable.
catalog_id() {
  curl -s -X POST $API/api/items -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
    -d "{\"displayName\":\"$2\",\"prefabClassName\":\"$1\"}" > /dev/null
  curl -s $API/api/items -H "Authorization: Bearer $BRIDGE_TOKEN" \
    | jq "[i['itemId'] for i in d['items'] if i['prefabClassName']=='$1'][0]"
}
ITEM_ID=$(catalog_id Medical_Bandage Bandage)
CRATE_ITEM_ID=$(catalog_id Supply_Crate "Supply Crate")

echo "character=$CHARACTER_ID bandage=$ITEM_ID crate=$CRATE_ITEM_ID"
```

### Step 1: read the limits, hardcode nothing

```sh
curl -s $API/api/inventory/limits -H "Authorization: Bearer $BRIDGE_TOKEN" | python3 -m json.tool
```

Returns the document shown under [Limits](#limits-hardcode-nothing), including
`"snapshotRequestBurst": 120`, `"snapshotRequestsPerMinute": 600`, `"unknownPrefabRequestBurst": 20` and
`"unknownPrefabRequestsPerMinute": 2`.

### Step 2: get some instances, and adopt them

A gather mints the ids (a shop purchase returns the same `grantedInstances` shape — see
[World](./world.md#gathering)). One crate and three bandages:

```sh
CRATE=$(curl -s -X POST $API/api/gathering/actions \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"action\":\"MinedOreDeposit\",\"itemId\":\"$CRATE_ITEM_ID\",\"quantity\":1}" \
  | jq "d['grantedInstances'][0]['instanceId']")

read -r B1 B2 B3 <<< "$(curl -s -X POST $API/api/gathering/actions \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"action\":\"MinedOreDeposit\",\"itemId\":\"$ITEM_ID\",\"quantity\":3}" \
  | jq "' '.join(g['instanceId'] for g in d['grantedInstances'])")"

echo "crate=$CRATE bandages=$B1 $B2 $B3"
```

Those four ids are what the mod seeds into each entity's persistence component **before** registering it
— see [the adoption rule](#the-adoption-rule-the-single-most-important-rule-here). Having spawned them,
ack:

```sh
curl -s -X POST $API/api/inventory/acks \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"acks\":[{\"instanceId\":\"$CRATE\",\"children\":[]},{\"instanceId\":\"$B1\",\"children\":[]},
                 {\"instanceId\":\"$B2\",\"children\":[]},{\"instanceId\":\"$B3\",\"children\":[]}]}" \
  | python3 -m json.tool
```

```jsonc
[ { "instanceId": "8bf22ef3-…", "outcome": "Cleared", "children": [] },
  { "instanceId": "c0dac370-…", "outcome": "Cleared", "children": [] },
  { "instanceId": "4b46fb94-…", "outcome": "Cleared", "children": [] },
  { "instanceId": "48cba1e0-…", "outcome": "Cleared", "children": [] } ]
```

All four are now live carried items. A helper to watch that, used from here on:

```sh
held() { curl -s $API/api/inventory/characters/$CHARACTER_ID/items -H "Authorization: Bearer $BRIDGE_TOKEN" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(len(d),'rows:',[(x['instanceId'][:8],x['parent']['kind'],x['revision']) for x in d])"; }
held
```

```
4 rows: [('8bf22ef3', 'Character', 0), ('c0dac370', 'Character', 0), ('4b46fb94', 'Character', 0), ('48cba1e0', 'Character', 0)]
```

### Step 3: a `Partial` batch, and its replay

The player puts two bandages into the crate. Note `BATCH` is captured, because the replay has to reuse
it:

```sh
BATCH=$(cat /proc/sys/kernel/random/uuid)
partial_body() {
cat <<JSON
{"batchId":"$BATCH","scope":{"kind":"Character","characterId":"$CHARACTER_ID"},"mode":"Partial",
 "upserts":[
   {"instanceId":"$B1","revision":1,"itemId":"$ITEM_ID","parent":{"kind":"Container","containerInstanceId":"$CRATE","slot":"0"},"attributes":{}},
   {"instanceId":"$B2","revision":1,"itemId":"$ITEM_ID","parent":{"kind":"Container","containerInstanceId":"$CRATE","slot":"1"},"attributes":{}}
 ],"deletes":[]}
JSON
}

curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(partial_body)" | python3 -m json.tool
```

```jsonc
{ "batchId": "7a05a7bb-…", "sequence": null, "applied": 2, "skippedNoOp": 0,
  "deleted": 0, "cascadeDeleted": 0, "swept": 0, "rejected": [], "replayOfPriorBatch": false }
```

Now send the **identical request again** — this is exactly what a Bridge does after a timeout:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(partial_body)" | python3 -m json.tool
```

```jsonc
{ "batchId": "7a05a7bb-…", "sequence": null, "applied": 2, "skippedNoOp": 0,
  "deleted": 0, "cascadeDeleted": 0, "swept": 0, "rejected": [], "replayOfPriorBatch": true }
```

Byte-identical apart from `replayOfPriorBatch`. Nothing was applied twice.

### Step 4: per-instance rejections do not fail the batch

One upsert naming an id the backend never issued, and one carrying `durability: 2.5`:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"batchId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"scope\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"mode\":\"Partial\",
       \"upserts\":[
         {\"instanceId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"revision\":1,\"itemId\":\"$ITEM_ID\",\"parent\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"attributes\":{}},
         {\"instanceId\":\"$B3\",\"revision\":1,\"itemId\":\"$ITEM_ID\",\"parent\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"durability\":2.5,\"attributes\":{}}
       ],\"deletes\":[]}" | python3 -m json.tool
```

```jsonc
{ "batchId": "7cb35f02-…", "sequence": null, "applied": 0, "skippedNoOp": 0,
  "deleted": 0, "cascadeDeleted": 0, "swept": 0,
  "rejected": [
    { "instanceId": "48cba1e0-…", "reason": "ValueOutOfRange" },
    { "instanceId": "433b7c37-…", "reason": "UnknownInstance" }
  ],
  "replayOfPriorBatch": false }
```

Still a `200`. `applied + skippedNoOp + rejected == 2 == upserts.length`, and note the rejected
`$B3` is **not** swept or altered — a rejection is not a deletion.

### Step 5: a `Full` reconcile sweeps what it omits

Report the crate and the two bandages inside it, and omit `$B3`, which the character is still carrying:

```sh
SEQ=$(( $(date +%s) * 1000 ))   # milliseconds — see "sequence" above for why not Ticks
FULL_BATCH=$(cat /proc/sys/kernel/random/uuid)
full_body() {
cat <<JSON
{"batchId":"$1","scope":{"kind":"Character","characterId":"$CHARACTER_ID"},"sequence":$2,"mode":"Full",
 "upserts":[
   {"instanceId":"$CRATE","revision":1,"itemId":"$CRATE_ITEM_ID","parent":{"kind":"Character","characterId":"$CHARACTER_ID"},"attributes":{}},
   {"instanceId":"$B1","revision":2,"itemId":"$ITEM_ID","parent":{"kind":"Container","containerInstanceId":"$CRATE","slot":"0"},"attributes":{}},
   {"instanceId":"$B2","revision":2,"itemId":"$ITEM_ID","parent":{"kind":"Container","containerInstanceId":"$CRATE","slot":"1"},"attributes":{}}
 ],"deletes":[]}
JSON
}

curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(full_body $FULL_BATCH $SEQ)" | python3 -m json.tool
held
```

```jsonc
{ "batchId": "b1169b36-…", "sequence": 1787900159000, "applied": 3, "skippedNoOp": 0,
  "deleted": 0, "cascadeDeleted": 0, "swept": 1, "rejected": [], "replayOfPriorBatch": false }
```
```
3 rows: [('4b46fb94', 'Container', 2), ('8bf22ef3', 'Character', 1), ('c0dac370', 'Container', 2)]
```

`swept: 1` — the omitted bandage, gone. Replaying the same `batchId` returns the same body with
`replayOfPriorBatch: true` and sweeps nothing further:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(full_body $FULL_BATCH $SEQ)" | jq "d['replayOfPriorBatch'], d['swept']"
```

Now the three ways to get `sequence` wrong. A new `batchId` with a lower sequence:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(full_body $(cat /proc/sys/kernel/random/uuid) $((SEQ-1000)))" | python3 -m json.tool
```

```jsonc
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "stale_sequence: sequence must be greater than the last applied sequence 1787900159000 for this scope",
  "status": 409, "retryable": false, "lastAppliedSequence": 1787900159000 }
```

A `DateTime.Ticks`-shaped sequence — refused before anything is read, on the very first batch:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "$(full_body $(cat /proc/sys/kernel/random/uuid) 638000000000000000)" | python3 -m json.tool
```

```jsonc
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "sequence_out_of_range: 638000000000000000 must be within 0..1000000000000000",
  "status": 400, "retryable": false }
```

And a scope naming two anchors:

```sh
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"batchId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"scope\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\",\"containerInstanceId\":\"$CRATE\"},\"mode\":\"Partial\",\"upserts\":[],\"deletes\":[]}" \
  | python3 -m json.tool
```

```jsonc
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "scope.containerInstanceId must not be set when scope.kind is Character",
  "status": 400, "retryable": false }
```

### Step 6: a `Full` that mentions a container but not its contents

The rule most likely to cost you data. Same scope, a **higher** sequence, and a payload naming **only**
the crate — its two bandages go unreported:

```sh
SEQ2=$(( SEQ + 1000 ))
curl -s -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"batchId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"scope\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},
       \"sequence\":$SEQ2,\"mode\":\"Full\",
       \"upserts\":[{\"instanceId\":\"$CRATE\",\"revision\":2,\"itemId\":\"$CRATE_ITEM_ID\",\"parent\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"attributes\":{}}],
       \"deletes\":[]}" | python3 -m json.tool
held
```

```jsonc
{ "batchId": "706a61a2-…", "sequence": 1787900160000, "applied": 1, "skippedNoOp": 0,
  "deleted": 0, "cascadeDeleted": 0, "swept": 2, "rejected": [], "replayOfPriorBatch": false }
```
```
1 rows: [('8bf22ef3', 'Character', 2)]
```

`swept: 2`. Mentioning the crate claimed knowledge of its contents; omitting them said they were gone.
Had the batch not mentioned the crate at all, nothing inside it would have been touched — and the crate
itself survives either way, because a container a surviving row sits in is never swept.

### Step 7: what a binder-level failure looks like

Worth running once, because it is the one 4xx that is not a problem document. A `revision` sent as a
string:

```sh
curl -s -D - -o /dev/null -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"batchId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"scope\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"mode\":\"Partial\",
       \"upserts\":[{\"instanceId\":\"$CRATE\",\"revision\":\"one\",\"itemId\":\"$CRATE_ITEM_ID\",\"parent\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"attributes\":{}}],\"deletes\":[]}" \
  | grep -iE "^HTTP|^content-type"

# ...and the engine's own Content-Type, forwarded verbatim instead of absorbed:
curl -s -D - -o /dev/null -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/x-www-form-urlencoded" \
  -d '{}' | grep -iE "^HTTP"
```

```
HTTP/1.1 400 Bad Request
Content-Type: text/plain; charset=utf-8
HTTP/1.1 415 Unsupported Media Type
```

Neither carries `retryable`, and neither is `application/problem+json` — see
[Errors](#errors-per-batch) for why a client must check `Content-Type` before parsing a 4xx.

### Step 8: the rate limits

**This step is last on purpose, and it needs a rested limiter.** Once you drain a bucket it refills at
the sustained rate — ten minutes for the unknown-prefab bucket's 20 tokens — and everything you send in
the meantime is a `429`, including the other steps above. If you have already run this step, restart
`src/Api` before running it again: the limiter is in-process, so a restart resets every bucket. (That
restart is also [the caveat](#the-limit-is-per-api-instance-not-per-deployment) made visible — bucket
state lives in one process and nowhere else.)

The unknown-prefab bucket is small enough to hit with a sequential loop — 25 reports against a bucket of
20:

```sh
for i in $(seq 1 25); do
  curl -s -o /dev/null -w "%{http_code} " -X POST $API/api/inventory/unknown-prefabs \
    -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
    -d "{\"sightings\":[{\"prefabClassName\":\"Modded_Rifle_$i\",\"count\":1,\"firstSeenAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}]}"
done; echo
```

```
202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 202 429 429 429 429 429
```

The first 20 drain the bucket; the rest are refused. The headers and body of one:

```sh
curl -s -D - -o /tmp/429.json -X POST $API/api/inventory/unknown-prefabs \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"sightings":[{"prefabClassName":"Modded_Rifle_X","count":1,"firstSeenAt":"2026-08-28T06:00:00Z"}]}' \
  | grep -iE "^HTTP|^retry-after|^content-type"; cat /tmp/429.json
```

```
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 30
{"title":"rate_limited: too many requests from this client — wait for the interval in the Retry-After header and resend unmodified","status":429,"retryable":true}
```

The snapshot bucket will **not** trip under a sequential loop, and that is the point — 10 tokens/second
replenish faster than a round trip consumes them, so an honest client never reaches it. Fire 200
concurrently and it does:

```sh
snap() { curl -s -o /dev/null -w "%{http_code}\n" -X POST $API/api/inventory/snapshots \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"batchId\":\"$(cat /proc/sys/kernel/random/uuid)\",\"scope\":{\"kind\":\"Character\",\"characterId\":\"$CHARACTER_ID\"},\"mode\":\"Partial\",\"upserts\":[],\"deletes\":[]}"; }
export -f snap; export API BRIDGE_TOKEN CHARACTER_ID
seq 1 200 | xargs -P 40 -I{} bash -c snap | sort | uniq -c
```

```
    119 200
     81 429
```

**Do not expect that exact split.** It depends on how fast your machine issues 200 requests and
therefore how many tokens replenish mid-run — three runs here produced 119/81, 121/79 and 131/69
against the same bucket. The invariant is the one worth checking: roughly the burst size succeeds, the
rest are refused immediately, and none of them queue. Those `429`s carry `Retry-After: 1` — one
replenishment period, against the unknown-prefab bucket's 30.

## Related reading

- [World](./world.md) — the inventory model behind this contract: the grant path, the delivery loop,
  the undeliverable queue, why every write is a targeted patch.
- [Items](./items.md) — the catalog an `itemId` has to exist in.
- [Accounts](./accounts.md) — tokens, the game server registry, hive settings.
- [ARCHITECTURE.md §3.2](../ARCHITECTURE.md#32-bridge-service) — where the Bridge runs and what it is.
- [ARCHITECTURE.md §4.3](../ARCHITECTURE.md#43-player-identity-token-exchange) — the player token
  exchange, and why it must be made by the Bridge itself.
- [ARCHITECTURE.md §9c](../ARCHITECTURE.md#9c-api-client-generation-kiota) — the Kiota-generated client
  and how to regenerate it from a running Central API.
