# Banking

Banks, bank accounts (Personal or Corporate), and deposit/withdraw/transfer, plus transaction history. See [MIGRATION.md §7](../MIGRATION.md#7-migration-plan-feature-3--banking), [§9](../MIGRATION.md#9-corporate-bank-accounts-banking--companies) (Corporate accounts), and [§10](../MIGRATION.md#10-bank-account-transaction-history) (history).

Banking data is hive-wide — every server in the deployment sees the same banks and accounts, since one deployment is one hive of servers sharing one world. Reads now require the `gameserver:banking:write` scope too (previously unscoped).

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and a `characterId` from [Characters](./characters.md).

Players can't open banks. The banks are precooked: `Banking.Infrastructure/Common/BankSeed.cs` seeds them on startup under fixed ids, and adding a bank means adding it there (opening banks will come with a web UI). Today there is one, **Federal E-Life Bank**, code `FEBK`, id `0f6c2b0e-5d1a-4c3e-9b8f-2a7d4e1c6b90`. `GET /api/banks` lists them along with each bank's `code`.

Opening, depositing, withdrawing and transferring on a bank account need `gameserver:banking:write` (granted to `gameserver-dev`):

```sh
BANK_ID=0f6c2b0e-5d1a-4c3e-9b8f-2a7d4e1c6b90

curl -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"characterId":"<characterId from the create-character response>"}'
```

## Account numbers

Every account gets an 18-character number based on the Dutch IBAN, e.g. `EL90FEBK0123456789`, printed in groups of four as `EL90 FEBK 0123 4567 89`:

| Part | Length | Example | Rule |
|---|---|---|---|
| Prefix | 2 | `EL` | Fixed |
| Check digits | 2 | `90` | MOD-97, calculated as for a real IBAN |
| Bank code | 4 | `FEBK` | The bank's `code`; it never changes |
| Account | 10 | `0123456789` | Random, unique (enforced by a unique index) |

The API stores and returns the compact form, and clients group it into fours. Every backend operation, transfers included, is keyed on `bankAccountId`. To turn a number a player typed into that id, the bridge calls `GET /api/bank-accounts/by-number/{number}`. Spaces and letter case are ignored. A number with the wrong layout or check digits gets `400` without being looked up, and a valid number nobody holds gets `404`. The response is just `bankAccountId`, `bankId`, `number` and `type`, with no balance and no owner, because whoever typed the number need not be the holder.

```sh
curl "http://localhost:5100/api/bank-accounts/by-number/EL90%20FEBK%200123%204567%2089" -H "Authorization: Bearer $BRIDGE_TOKEN"
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
