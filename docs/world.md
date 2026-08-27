# World

Persists what a character carries and what the world looks like across gameserver restarts. Phase 1
(this doc) covers the grant path — shop purchases and gathering minting items — and the delivery loop
that hands them to the game; snapshot apply/reconcile and world-structure state are later phases.

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
me what you called it" — see [Delivering a granted instance](#delivering-a-granted-instance) below.

## Scopes

| Scope | Grants |
|---|---|
| `gameserver:inventory:read` | `GET /api/inventory/limits`, `.../characters/{id}/items`, `.../characters/{id}/pending` |
| `gameserver:inventory:write` | `POST /api/inventory/acks`, `.../instances/{id}/spawn-failed`, `POST /api/gathering/actions` (also reserved for the not-yet-built snapshot-apply endpoint) |
| `inventory:manage` | staff: `GET /api/inventory/undeliverable` today; the unknown-prefab queue, movement audit, item removal and prune land with later phases |

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
in a rifle, a SIM in a phone) gets an id at all, since the mod never mints one itself.

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

- [ARCHITECTURE.md §9e](../ARCHITECTURE.md#9e-modulith-structure--module-boundaries) — why
  `ItemInstance` is a plain document rather than an event-sourced aggregate, and a Marten gotcha
  (`Store()`+`Delete()` on the same id in one `SaveChangesAsync`) discovered while building the
  delivery loop.
- [Shops](./shops.md) and [Items](./items.md) — the catalog and the purchase path that feeds this
  module.
- [Skills](./skills.md) — the XP side of a gathering action.

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, which is on the Compose network — see the main [README](../README.md).)
