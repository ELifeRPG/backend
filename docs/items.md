# Items

A staff-curated catalog of purchasable game items — display name plus the ArmA Reforger prefab class name the gameserver spawns on a grant. See [MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan.

Item data is isolated per gameserver — reads also require the `gameserver:items:manage` scope, and an item created via one gameserver is invisible to every other gameserver in the same deployment.

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)). `POST /api/items`, `GET /api/items`, and `GET /api/items/{id}` all need the `gameserver:items:manage` scope (also granted to `gameserver-dev`) — `Items` only has the one scope, unlike `Banking`/`Companies`/`Shops`, which reuse a separate write scope for reads:

```sh
ITEM_ID=$(curl -s -X POST http://localhost:5100/api/items \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"displayName":"9mm Ammo Box","prefabClassName":"Ammo_9x19_Box"}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['itemId'])")

curl http://localhost:5100/api/items -H "Authorization: Bearer $BRIDGE_TOKEN"

curl http://localhost:5100/api/items/$ITEM_ID -H "Authorization: Bearer $BRIDGE_TOKEN"
```

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, connected to the Compose network — see the main [README](../README.md).)
