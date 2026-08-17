# Cross-module atomic writes

## Context

Every module (`Accounts`, `Characters`, `Banking`, `Companies`) owns its own Marten store/schema, and `ARCHITECTURE.md §9e` restricts cross-module `Application → Application` access to a small set of read-only "public surface" queries (`CompanyMemberPermissionsQuery` etc.). There is no mechanism today for a single operation to write to two modules' event streams atomically. `ARCHITECTURE.md §8` reserves "a saga/process-manager approach" for this case without designing it.

The concrete feature forcing this design: a character buys shares in a company using money from their bank account. That's a genuine two-module write — `Banking` must debit a `BankAccount`, `Companies` must credit the buyer with shares — and both must succeed or neither must, per the product requirement that a player must never have money debited without receiving what they paid for (or vice versa).

This spec designs the general **mechanism** for atomic cross-module writes, using the share-purchase flow as the driving (and first) use case. It reuses an insight already true of this deployment: every module's data lives in one physical PostgreSQL database, just separate schemas — so a real ACID transaction across modules is possible without a saga, as long as we don't paper over that fact by hiding it behind separate connections.

## Goals

- A named, reusable pattern for "operation touches N modules' aggregates, all commit or none do" — not a one-off hack for share purchases.
- No window where one module's write is committed and another's isn't, even under a mid-operation crash or Postgres connection loss.
- Preserve the existing module-boundary rule (`Domain`/`Infrastructure`/`Api` never cross-reference); the mechanism only ever touches the `Application` layer, and only for named handlers, not a general escape hatch.
- Reuse existing per-module repository implementations (`MartenBankAccountRepository`, `MartenCompanyRepository`, ...) rather than duplicating persistence logic for the cross-module case.

## Non-goals

- A full company-shares domain model (share classes, cap tables, secondary trading, dynamic pricing). `Company` gains only the minimum needed to exercise the mechanism: a fixed `SharePrice` and an `IssueShares` operation. Anything richer is a separate, later spec.
- A saga/process-manager/event-driven eventual-consistency mechanism. Rejected for this use case — see "Alternatives considered".
- Generalizing beyond two modules per operation, or nesting cross-module transactions. Not needed by the driving use case; add it if a real third-module scenario shows up.
- Making this mechanism available to `Domain` or `Infrastructure` code. It's an `Application`-layer concern only.

## Alternatives considered

**Saga / process manager with compensation** (`ARCHITECTURE.md §8`'s original placeholder): withdraw money in one committed transaction, then attempt the share grant, compensating (refund) on failure. Rejected because it necessarily has a window where money is withdrawn and shares aren't granted yet, which directly violates the product requirement above, and because it requires building retry/compensation infrastructure (there is no message bus or background worker in this codebase today) for a single feature.

**Single-module write, other module only validates** (model share purchase as a pure `Banking` write with `Companies` only answering a read query, the way `BankAccountAuthorization` already asks `Companies` for permissions): rejected because share ownership needs its own auditable event stream in `Companies` — a share is a `Companies` concept, not a side-effect note on a `BankAccountWithdrawn` event.

**Chosen: shared-transaction coordinator.** All modules share one Postgres database. Marten (9.23) supports enlisting a session in an externally supplied `NpgsqlTransaction` via `SessionOptions.ForTransaction(NpgsqlTransaction, shouldAutoCommit: false)` — confirmed against the installed package, not assumed. That means two modules' sessions can be flushed into one Postgres transaction and committed once, giving real atomicity with no compensation logic.

## Mechanism

### `Shared.Integration` (new project, split in two to keep Marten out of `Application`)

**`Shared.Integration.Abstractions`** — referenced by any module's `*.Application` project that participates in cross-module writes:

```csharp
public sealed class CrossModuleSessionHandle
{
    // Opaque to Application code — no public members. Only Shared.Integration's
    // Infrastructure-facing package and a module's own Infrastructure code can
    // unwrap the underlying NpgsqlTransaction (via an internal accessor / InternalsVisibleTo,
    // exact mechanism TBD at implementation time — see Open items).
}

public interface ICrossModuleTransaction : IAsyncDisposable
{
    CrossModuleSessionHandle Handle { get; }
    Task CommitAsync(CancellationToken ct);   // rolls back automatically on dispose if not committed
}

public interface ICrossModuleTransactionFactory
{
    ICrossModuleTransaction Begin();
}
```

**`Shared.Integration`** (Infrastructure-level, references Npgsql + Marten) — `NpgsqlCrossModuleTransaction : ICrossModuleTransaction`, owning one `NpgsqlConnection` + `NpgsqlTransaction` against the shared connection string. Registered once at the composition root (`src/Api/Program.cs`).

### Module-side participation contract

A module that wants to be writable inside a cross-module transaction exposes one small factory from its own `Application` layer, e.g. in `Companies.Application`:

```csharp
public interface ICompanyRepositoryFactory
{
    ICompanyRepository CreateFor(CrossModuleSessionHandle handle);
}
```

implemented in `Companies.Infrastructure`:

```csharp
public sealed class MartenCompanyRepositoryFactory(ICompaniesStore store) : ICompanyRepositoryFactory
{
    public ICompanyRepository CreateFor(CrossModuleSessionHandle handle)
    {
        var session = store.OpenSession(SessionOptions.ForTransaction(handle.Unwrap(), shouldAutoCommit: false));
        return new MartenCompanyRepository(session); // same repository class used for ordinary requests
    }
}
```

This keeps construction of `Companies`' own repository entirely inside `Companies` — the coordinator never reaches into another module's `Infrastructure`, only calls a factory that module chose to expose from its own `Application` layer. `Banking.Application` gains an equivalent `IBankAccountRepositoryFactory` for the same reason (used by the orchestrating command below to get a transaction-bound `BankAccount` repository instead of its normal per-request one).

### Orchestrating command

`PurchaseCompanySharesCommand(BankAccountId PayerBankAccountId, CharacterId Buyer, CompanyId CompanyId, int Quantity, decimal PricePerShare)` lives in `Banking.Application` — Banking already depends on `Companies.Application` in the existing DAG, so no new dependency direction is introduced. Handler:

1. `ICrossModuleTransactionFactory.BeginAsync()`.
2. `IBankAccountRepositoryFactory.CreateFor(handle)` → load the buyer's `BankAccount`, validate sufficient balance (`Quantity * PricePerShare`), append `BankAccountWithdrawn`.
3. `ICompanyRepositoryFactory.CreateFor(handle)` (injected from `Companies.Application`) → load `Company`, append `CompanySharesIssued(CompanyId, Buyer, Quantity)`.
4. `SaveChangesAsync` on both repositories (flushes pending appends into the shared transaction; does not commit, per `shouldAutoCommit: false`).
5. `transaction.CommitAsync()` — one Postgres commit, both streams updated together.

Any exception before step 5 (business-rule failure or infrastructure failure) leaves the `NpgsqlTransaction` uncommitted; disposal rolls it back. Nothing is persisted to either module.

## Domain model (illustrative — minimum needed to exercise the mechanism)

`Company` gains:

```
Shares: IReadOnlyDictionary<CharacterId, int>   // built by the projection from CompanySharesIssued
```

**Refined during planning:** no `SharePrice` field on `Company`. Storing one would require its own sub-feature (who sets it, when) that isn't otherwise needed — and this spec's own Non-goals already rule out designing pricing policy. `PurchaseCompanySharesCommand` instead takes `PricePerShare` as a caller-supplied parameter; `Company` only ever records quantities issued.

New event: `CompanySharesIssued(CompanyId Id, CharacterId Buyer, int Quantity)`, appended by a new domain method `Company.IssueShares(CharacterId buyer, int quantity)`, following the same `Create`/`Apply` convention as every other aggregate in this codebase.

No changes to `BankAccount`/`Banking` — `PurchaseCompanySharesCommand`'s step 2 reuses the existing `BankAccount.Withdraw` domain method and `BankAccountWithdrawn` event, exactly as `WithdrawCommand` does today, just invoked from a different Application-layer command.

## Error handling

- Business-rule failures (insufficient balance, company not found, zero/negative quantity) are domain guard exceptions raised before or during step 2/3, caught by the handler, mapped to a 4xx the same way single-module domain exceptions are today (`ARCHITECTURE.md §9e`'s existing convention). The transaction is never committed.
- Infrastructure failures (Postgres connection drop mid-operation) surface as an unhandled exception from `SaveChangesAsync`/`CommitAsync`; the transaction rolls back automatically. There is exactly one transaction boundary, so there is no partial-success state to detect or clean up — this is what satisfies "never leave money/state stuck mid-flight," structurally, not by convention.
- If `CommitAsync` itself fails after both `SaveChangesAsync` calls succeeded (e.g. connection drops between flush and commit), Postgres guarantees the transaction never committed — both writes are absent, not half-present. The caller should treat this as a transient failure safe to retry from scratch.

## Testing

- `Banking.Application.UnitTests`: `PurchaseCompanySharesCommand` handler logic (balance check, quantity validation) against fake repository factories.
- New `Banking.IntegrationTests` case, using this repo's existing local-infra-stack pattern (`docker compose up -d`, not Testcontainers): happy path (both `BankAccount` and `Company` streams show the new events after commit); forced failure inside step 3 (throw before `CompanySharesIssued` is appended) → reload the `BankAccount` from Postgres afterward and assert `BankAccountWithdrawn` was **not** persisted — this is the test that actually proves atomicity, not just that an exception was thrown.
- No changes needed to existing single-module tests; the mechanism is additive.

## Boundary discipline

Add to `ARCHITECTURE.md §9e`, next to the existing query-only cross-module rule: cross-module **writes** are only possible through an explicitly named repository factory a module chooses to expose from its own `Application` layer, invoked only from inside an `ICrossModuleTransaction`. `Domain`, `Infrastructure`, and `Api` projects remain fully isolated per module, unchanged. This is not a general mechanism for one module to reach into another — each new cross-module write needs its own named orchestrating command and its own explicit review, the same way today's four read-only "public surface" queries are each individually named and reviewed rather than a generic query-anything API.

## Open items — resolved in the implementation plan

All three items below were open when this spec was written; see `docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md` for the concrete resolution used by every task.

- `CrossModuleSessionHandle.Unwrap()` visibility: resolved as `internal object RawTransaction` on the handle (`Shared.Integration.Abstractions`) plus `[assembly: InternalsVisibleTo("ELifeRPG.Shared.Integration")]`, with a **public** `Unwrap()` extension method in `Shared.Integration` that module `Infrastructure` factories call — no module ever needs its own `InternalsVisibleTo` grant.
- Connection string source: confirmed identical today (`Host=postgres;Database=postgres;...` for all four existing databases) via `src/Api/appsettings.Development.json` and `tests/Banking.IntegrationTests/TestServices.cs`. `Shared.Integration` gets its own explicit `SharedDatabase` key (same value) rather than reusing `BankingDatabase`, so it isn't accidentally coupled to one module's config.
- `ICrossModuleTransactionFactory` lifetime: resolved as `Singleton` — the factory is stateless besides the connection string; every `BeginAsync` call opens its own fresh `NpgsqlConnection`/`NpgsqlTransaction`, so there's no shared mutable state or double-open risk to manage per-scope.

## Decisions log

- Chose a shared-transaction coordinator over a saga/process-manager, because the product requirement ("never leave money/state stuck mid-flight") rules out any design with a partial-success window, and because all modules already share one physical Postgres database, making true atomicity available without new infrastructure (no message bus/retry worker needed).
- Kept the existing boundary rule's shape (`Application → Application` only) rather than opening a new kind of cross-module reference — cross-module writes go through the same layer as cross-module reads, just via a factory a module explicitly exposes instead of a query it explicitly exposes.
- Scoped the domain-model changes to the minimum needed to exercise the mechanism (`SharePrice`, `Shares`, one event) rather than designing a full company-shares feature in this spec.
- Orchestrating command lives in `Banking.Application`, not `Companies.Application`, because the existing dependency DAG already has `Banking → Companies` and not the reverse — no new dependency direction introduced.
- Dropped the `SharePrice` field on `Company` sketched in the first draft — `PricePerShare` is a caller-supplied parameter on `PurchaseCompanySharesCommand` instead, since storing a price would require its own setter mechanism this spec's Non-goals already rule out designing.
