# Items

A staff-curated catalog of game items — display name plus the ArmA Reforger prefab class name the gameserver spawns on a grant. See [MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan.

Item data is hive-wide — reads also require the `gameserver:items:manage` scope, and an item created via one gameserver is visible from every other gameserver in the same deployment: the catalog is one shared list for the whole hive, not one per map.

## The catalog decides what persists

The catalog is not only a shopping list. World persistence refuses to store an item whose prefab has no catalog entry, so **an uncatalogued prefab does not survive a server restart** (see [World](./world.md)). Two consequences worth knowing before you use this module:

- A fresh deployment persists nothing until prefabs have been imported. That is what `POST /api/items/bulk` is for.
- `prefabClassName` is **unique** across the catalog. World persistence resolves a prefab to exactly one `itemId`, so a second entry claiming the same prefab would make every instance of it ambiguous. A duplicate is rejected with `409`, and bulk import treats an already-known prefab as a no-op rather than an error.

Each entry also carries one field the gameserver needs:

| Field | Meaning |
|---|---|
| `persistence` | `Despawns` (default) — instances dropped on the ground get a TTL and are eventually reclaimed. `Persistent` — never swept while lying in the world; use it for vehicles and deployables, where a TTL would be a bug. |

There is no stack size, because Reforger has no item stacking: every item is a discrete entity, and a
magazine is one entity carrying an integer ammo count rather than a container of rounds.

## Walkthrough

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)). Every route here needs the `gameserver:items:manage` scope (also granted to `gameserver-dev`) — `Items` only has the one scope, unlike `Banking`/`Companies`/`Shops`, which reuse a separate write scope for reads:

```sh
ITEM_ID=$(curl -s -X POST http://localhost:5100/api/items \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"displayName":"9mm Ammo Box","prefabClassName":"Ammo_9x19_Box"}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['itemId'])")

curl http://localhost:5100/api/items/$ITEM_ID -H "Authorization: Bearer $BRIDGE_TOKEN"
```

Creating a second entry for `Ammo_9x19_Box` now fails with `409 Prefab class name already in the catalog`, and the problem detail names the item that already holds it.

### Bulk import

`POST /api/items/bulk` registers many prefabs at once and is **idempotent on `prefabClassName`** — re-running the same import creates nothing the second time and returns the existing ids. Prefabs already in the catalog are left untouched, including their display name: this endpoint registers prefabs, it never redefines them. `displayName` is optional and falls back to the prefab class name, so a raw prefab dump imports without a curation pass.

```sh
curl -s -X POST http://localhost:5100/api/items/bulk \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"items":[
        {"prefabClassName":"Medical_Bandage","displayName":"Bandage"},
        {"prefabClassName":"Vehicle_Pickup","displayName":"Pickup Truck","persistence":"Persistent"},
        {"prefabClassName":"Ammo_762x39_Box"}
      ]}'
```

```jsonc
{
  "created": 3,
  "alreadyPresent": 0,
  "results": [ { "prefabClassName": "Medical_Bandage", "itemId": "…", "created": true }, … ]
}
```

At most 1000 items per request — the whole import is one transaction, so the cap doubles as a lock-duration cap. Naming the same prefab twice in one payload is a `400`: which of the two definitions won would otherwise be invisible.

### Listing, and the catalog version

```sh
curl http://localhost:5100/api/items -H "Authorization: Bearer $BRIDGE_TOKEN"
```

```jsonc
{ "catalogVersion": 42, "items": [ … ] }
```

`catalogVersion` is opaque and monotonic — it carries no meaning beyond "a different value means your copy is stale". The Bridge fetches the catalog at boot and re-fetches when the version changes, which is why the world-persistence wire format carries `itemId` rather than `prefabClassName`: prefab resolution never has to happen on the write path.

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, which is on the Compose network — see the main [README](../README.md).)
