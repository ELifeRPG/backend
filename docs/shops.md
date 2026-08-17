# Shops

A shop, owned by a Character (Personal) or a Company (Corporate), lists catalog items ([Items](./items.md)) for sale at a price with finite stock. See [MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan.

Shop data is isolated per gameserver — reads also require the `gameserver:shops:write` scope, and a shop opened via one gameserver is invisible to every other gameserver in the same deployment.

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)), a `characterId` from [Characters](./characters.md), a payout `bankAccountId` from [Banking](./banking.md) (the shop's owner must already have opened an account there), and an `itemId` from [Items](./items.md).

`POST /api/shops` needs the `gameserver:shops:manage` scope; managing listings and reads (`GET /api/shops`, `GET /api/shops/{id}`) need `gameserver:shops:write` (both also granted to `gameserver-dev`):

```sh
SHOP_ID=$(curl -s -X POST http://localhost:5100/api/shops \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"ownerType\":\"Personal\",\"ownerCharacterId\":\"$CHARACTER_ID\",\"displayName\":\"Joe's Guns\",\"payoutBankAccountId\":\"$BANK_ACCOUNT_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['shopId'])")

LISTING_ID=$(curl -s -X POST http://localhost:5100/api/shops/$SHOP_ID/listings \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"itemId\":\"$ITEM_ID\",\"price\":5,\"stock\":10,\"actingCharacterId\":\"$CHARACTER_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['listingId'])")

curl http://localhost:5100/api/shops/$SHOP_ID -H "Authorization: Bearer $BRIDGE_TOKEN"
```

## Corporate shops

A shop can belong to a company instead — provide `companyId` instead of `characterId`, and a payout account opened via [Banking's Corporate accounts](./banking.md#corporate-accounts). Managing a Corporate shop's listings requires the acting character to hold the company's `ManageShops` permission (granted to the founder's "Owner" position by default; see [Companies](./companies.md)) — a plain member gets `403`.

## Purchasing

`POST /api/shops/{shopId}/listings/{listingId}/purchase` needs the `gameserver:shops:write` scope. Payment settles immediately via [Banking](./banking.md) — the buyer needs their own bank account with enough balance:

```sh
curl -X POST http://localhost:5100/api/shops/$SHOP_ID/listings/$LISTING_ID/purchase \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"quantity\":2,\"buyerCharacterId\":\"$BUYER_CHARACTER_ID\",\"buyerBankAccountId\":\"$BUYER_BANK_ACCOUNT_ID\"}"
```

A purchase either fully succeeds or fully fails against the stock read at the moment it's processed — no reservation/holding across separate requests. `409` covers not-enough-stock, not-enough-balance, and the rare case where two purchases raced for the same listing (retry the request). `403` means `buyerCharacterId` doesn't own (or, for a Corporate account, doesn't hold `ManageFinances` on) `buyerBankAccountId`.

## Live updates

`hubs/shops` is a SignalR hub pushing listing changes in real time — connect with a bearer token via the `access_token` query parameter (browsers can't set a custom header on a WebSocket upgrade), call `SubscribeToShop(shopId)`/`UnsubscribeFromShop(shopId)` to join/leave a shop's update group, and listen for `ListingChanged`/`ListingRemoved` messages. This is a delivery convenience, not the source of truth — after a disconnect, re-fetch `GET /api/shops/{shopId}` to resync rather than expecting to catch up over the hub.

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, connected to the Compose network — see the main [README](../README.md).)
