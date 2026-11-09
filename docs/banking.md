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

## Amounts and fees

Money is held in whole cents. A deposit, withdrawal, transfer, share price or shop listing price must be greater than zero and have at most two decimals. Anything else gets `400` and books nothing. `1.50` and `1.500` are both accepted; `1.005` is not. A transfer to the account it comes from also gets `400`.

Each booking is charged a fee of `transactionFeeBase + amount × transactionFeeMultiplier`. The account keeps the terms its bank had when the account was opened. The fee is rounded to whole cents half away from zero when the booking is made, so `0.205` is booked as `0.21` and `0.2049` as `0.20`. `amount` plus `fee` therefore always equals the change in balance exactly. The rule is implemented in `Shared.Kernel/Money.cs`.

Every bank account response carries those terms as `transactionFeeBase` and `transactionFeeMultiplier`. Previewing a fee from them with the same rounding gives exactly the fee that will be booked, even after the bank's own terms (`GET /api/banks`) have changed.

## Account numbers

Every account gets an 18-character number based on the Dutch IBAN, e.g. `EL90FEBK0123456789`, printed in groups of four as `EL90 FEBK 0123 4567 89`:

| Part | Length | Example | Rule |
|---|---|---|---|
| Prefix | 2 | `EL` | Fixed |
| Check digits | 2 | `90` | MOD-97, calculated as for a real IBAN |
| Bank code | 4 | `FEBK` | The bank's `code`; it never changes |
| Account | 10 | `0123456789` | Random, unique (enforced by a unique index) |

The API stores and returns the compact form, and clients group it into fours. Every backend operation, transfers included, is keyed on `bankAccountId`. To turn a number a player typed into that id, the bridge calls `GET /api/bank-accounts/lookup-id?number={number}`. Spaces and letter case are ignored. A missing number, or one with the wrong layout or check digits, gets `400` without being looked up. Otherwise the response is a list with one element, or an empty list if nobody holds the number. Each element is just `bankAccountId`, `bankId`, `number` and `type`, with no balance and no owner, because whoever typed the number need not be the holder.

```sh
curl -G http://localhost:5100/api/bank-accounts/lookup-id --data-urlencode "number=EL90 FEBK 0123 4567 89" -H "Authorization: Bearer $BRIDGE_TOKEN"
```

## Corporate accounts

A bank account can belong to a character *or* a company — provide exactly one of `characterId`/`companyId` (see [Companies](./companies.md) for creating one first). The founder joins a new company in the "Owner" position (all `CompanyPermissions`), so only the founder (or anyone else later added to "Owner") can withdraw/transfer on a Corporate account; a plain member ("Rookie", the default when adding a member) gets `403`:

```sh
curl -X POST http://localhost:5100/api/banks/$BANK_ID/accounts \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"companyId":"<companyId from the create-company response>"}'
```

Every bank account response carries `companyName` next to `companyId`: the owning company's name for a Corporate account, `null` for a Personal one.

## Accounts a character may manage

`GET /api/bank-accounts?managedBy={characterId}` lists every account the character may withdraw from or transfer out of: their Personal accounts, plus the accounts of every company in which their position includes `ManageFinances`. It is the same rule the bookings check (`BankAccountAuthorization.RequiredCompanyPermission`), so an account is listed exactly when a withdrawal by that character would be authorized, and a change to their membership shows up in the next response. Each element is a full bank account, with `companyName` set on the company ones. `managedBy` is required; without it the response is `400`.

```sh
curl "http://localhost:5100/api/bank-accounts?managedBy=$CHARACTER_ID" -H "Authorization: Bearer $BRIDGE_TOKEN"
```

## Bookings

Deposits, withdrawals, transfers and share purchases each create one booking with its own `transactionId`, returned in the response. A deposit names the depositing `characterId`, which is recorded but not checked against the account's owner. A missing or empty `characterId` gets `400`; on a withdrawal or transfer it gets `403`, like any character not allowed to use the account. Deposit, withdraw and transfer also accept an optional `source` of 1 to 128 characters, such as the ATM's id. Anything outside that range gets `400`.

A `409` from these endpoints means nothing was booked. It carries a `code` and a `retryable` flag:

| `code` | `retryable` | Meaning |
|---|---|---|
| `insufficient_balance` | `false` | The balance doesn't cover the amount plus the fee. Resending won't help. |
| `concurrent_modification` | `true` | Another booking on the account committed first. Resend the request. |

## Idempotency

Deposit, withdraw and transfer require an `Idempotency-Key` header: any string of 1 to 128 characters that is
unique per booking, e.g. the ATM's id, the server start and a counter. Without one, or with a longer one, the
request gets `400`.

```sh
curl -X PUT http://localhost:5100/api/bank-accounts/$BANK_ACCOUNT_ID/deposit \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: atm-17-1730995200-42" \
  -d '{"amount":100,"characterId":"<characterId>","source":"atm-17"}'
```

- **The same request again** with the same key, within 24 hours, books nothing and returns the first response
  byte for byte, including its `transactionId`. That's what makes a timeout recoverable: resend with the same
  key, and you either get the booking that already happened or the one you're making now.
- **The same key with a different body** (another amount, account, character, source or operation) gets `422`
  with `code: "idempotency_key_reused"` and `retryable: false`. Nothing is booked.
- **Two requests with the same key at once** book once. Both get the same answer.
- **Keys are scoped to the calling game server**, as `{gameServerId}:{key}`. Two servers can use the same key
  without affecting each other.
- **Only successful bookings are remembered.** A refused request (`insufficient_balance`, `403`, `404`, ...)
  booked nothing, so sending it again with the same key is evaluated again.
- **After 24 hours** the key is forgotten, and the same key books again.

Share purchases don't take a key.

## Transaction history

`GET /api/bank-accounts/{id}/transactions` lists an account's most recent deposits/withdrawals/transfers (newest first, capped at 30). A transfer carries the other account's `counterpartyNumber` (and its `counterpartyBankAccountId`), so a statement can show who the money went to or came from. Every row also carries its booking's `transactionId` (both rows of a transfer share it), the `gameServerId` that made it, its `source`, and the `actingCharacterId`: the depositor, the withdrawer or the sender. The receiving side of a transfer has no acting character. The endpoint needs no scope beyond authentication, the same as the account details endpoint:

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
