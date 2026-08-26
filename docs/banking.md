# Banking

Banks, bank accounts (Personal or Corporate), and deposit/withdraw/transfer, plus transaction history. See [MIGRATION.md §7](../MIGRATION.md#7-migration-plan-feature-3--banking), [§9](../MIGRATION.md#9-corporate-bank-accounts-banking--companies) (Corporate accounts), and [§10](../MIGRATION.md#10-bank-account-transaction-history) (history).

Banking data is hive-wide — every server in the deployment sees the same banks and accounts, since one deployment is one hive of servers sharing one world. Reads now require the `gameserver:banking:write` scope too (previously unscoped).

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and a `characterId` from [Characters](./characters.md).

`POST /api/banks` needs the `gameserver:banking:manage` scope; opening/depositing/withdrawing/transferring on a bank account needs `gameserver:banking:write` (both also granted to `gameserver-dev`):

```sh
BANK_ID=$(curl -s -X POST http://localhost:5100/api/banks \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"First E-Life Bank","transactionFeeBase":0.20,"transactionFeeMultiplier":0.02}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['bankId'])")

curl -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"characterId":"<characterId from the create-character response>"}'
```

## Corporate accounts

A bank account can belong to a character *or* a company — provide exactly one of `characterId`/`companyId` (see [Companies](./companies.md) for creating one first). The founder joins a new company in the "Owner" position (all `CompanyPermissions`), so only the founder (or anyone else later added to "Owner") can withdraw/transfer on a Corporate account; a plain member ("Rookie", the default when adding a member) gets `403`:

```sh
curl -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"companyId":"<companyId from the create-company response>"}'
```

## Transaction history

`GET /api/bank-accounts/{id}/transactions` lists an account's most recent deposits/withdrawals/transfers (newest first, capped at 30) — no scope beyond authentication, same as the account details endpoint:

```sh
curl http://localhost:5100/api/bank-accounts/$BANK_ACCOUNT_ID/transactions -H "Authorization: Bearer $BRIDGE_TOKEN"
```

## Company shares

`PUT /api/bank-accounts/{id}/purchase-company-shares` debits the account for `quantity * pricePerShare` and credits the acting character with shares in the given company, atomically — if either side would fail, neither is applied:

```sh
curl -X PUT http://localhost:5100/api/bank-accounts/$BANK_ACCOUNT_ID/purchase-company-shares \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"characterId":"<characterId>","companyId":"<companyId>","quantity":10,"pricePerShare":5.00}'
```

(These assume you're running `curl`/`dotnet run` from inside the devcontainer, which is on the Compose network — see the main [README](../README.md).)
