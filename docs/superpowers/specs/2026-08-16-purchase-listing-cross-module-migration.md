# `PurchaseListingCommand`: migrating off the saga onto `ICrossModuleTransaction`

## Context

`Shops.Application.Shops.PurchaseListingCommand` (see `docs/superpowers/specs/2026-08-15-shops-design.md`) was originally built as a saga: the handler reserved stock on the `ShopListing` aggregate, then dispatched to `Banking`'s `TransferCommand` to move payment, with a compensating `ShopListing.RestoreStock`/`RestoreStockAsync` path to undo the reservation if the payment leg failed. This was the right shape *at the time* — `ICrossModuleTransaction` (see `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`) didn't exist yet when `Shops` was designed, so a saga with compensation was the only available way to keep a cross-module write from landing in a partial state.

Once `ICrossModuleTransaction` shipped (first proven out via `Banking.Application.Companies.PurchaseCompanySharesCommand`), the saga's rationale no longer applied: both `Shops` and `Banking` live in the same physical Postgres database, just separate schemas, so the operation qualifies for the atomic coordinator per the rule in `ARCHITECTURE.md §9e`. The saga was never a rejected technical finding — it was correct given what was available — but it left a real window where a crash between the reservation and the payment leg could strand a reserved-but-unpaid-for stock decrement, with no way to guarantee the compensating restore actually ran. Migrating closes that window entirely: reservation and payment now commit in one shared Postgres transaction, so there is no partial-success state to compensate for.

## What changed

- Added `IShopListingRepositoryFactory` (and its Marten implementation, `MartenShopListingRepositoryFactory`), mirroring `ICompanyRepositoryFactory`/`IBankAccountRepositoryFactory` — obtains an `IShopListingRepository` bound to a shared `ICrossModuleTransaction`.
- Split the repository's purchase path into `ReserveStockAsync` (loads the listing, applies `ShopListing.Purchase`, but deliberately does not flush to Postgres) and `SaveChangesAsync` (flushes pending appends) — so a cross-module caller can reserve stock and settle payment against two separate repositories before one shared commit.
- Removed `ShopListing.RestoreStock` (domain) and the corresponding `RestoreStockAsync` repository method — the compensating-action path has no caller once there's no partial-success window to compensate for.
- Rewrote `PurchaseListingHandler` to reserve stock and settle payment inside one `ICrossModuleTransaction`, via `IShopListingRepositoryFactory` and `Banking.Application`'s `IBankAccountRepositoryFactory`, calling `BankAccount`'s domain methods directly instead of dispatching to `Banking`'s `TransferCommand` through a saga.
- Granted `Shops.Application` internal visibility into `Banking.Application.Common.BankAccountAuthorization`, needed to resolve payment authorization the same way `TransferCommand` does.
- Verified the concurrency mechanism before writing the real migration, via three spikes (see git history, commits `769ea5c`, `f826099`, `f1d20e3`): the first spike (`FetchForWriting`) and second spike (an explicit version-checked `Append`) were both **NO-GO** — Marten's optimistic-concurrency machinery doesn't compose with a `SessionOptions.ForTransaction`-bound session. The third spike — a Postgres row lock (`SELECT ... FOR UPDATE` on the listing's doc-table row) held for the rest of the transaction, followed by a plain unversioned `Append` — was the one that worked, and is what `ReserveStockAsync` uses on the cross-module repository path.

## Non-goals

- No shape changes to `PurchaseListingCommand`'s public request/response contract, `Shops.Api`, or `docs/shops.md` — this is an internal implementation migration only.
- `docs/superpowers/specs/2026-08-15-shops-design.md` and `MIGRATION.md` are left unmodified — they're historical records of the saga-era design, not living docs.
