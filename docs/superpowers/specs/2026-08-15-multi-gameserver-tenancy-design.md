# Multi-Gameserver Tenancy — Design

## Summary

Per `ARCHITECTURE.md` §4.1, a "tenant" is one full self-hosted ELifeRPG
deployment (its own gameserver fleet, Central API, Postgres, and Keycloak
instance). Within that one tenant, a real deployment already expects
multiple ArmA Reforger gameserver instances — per-instance OAuth clients are
a first-class concept (§3.2/§4.2), and `Accounts` already isolates correctly
per gameserver: `session-bootstrap` resolves the caller's own `client_id`
claim off its JWT and scopes whitelist approvals to it (`GameServer`,
`WhitelistApplication` in `Accounts.Domain`).

`Characters`, `Banking`, and `Companies` don't do this at all today — a
character (and its bank accounts, company memberships) created via one
gameserver is visible and usable from every other gameserver in the same
tenant. This spec closes that gap: `Character`, `Company`, `Bank`, and
`BankAccount` data becomes isolated per gameserver, using Marten's built-in
conjoined multi-tenancy, keyed by the calling gameserver's OAuth `ClientId`
— the same identifier `GameServer`/`WhitelistApplication` already use.
`Account` and `GameServer` stay untenanted/shared across a tenant's
gameservers, since a player's account and a server's own settings are
already correctly modeled as tenant-wide.

**Naming note:** this deliberately reuses the word "tenant" at a second,
narrower layer than §4.1 uses it — one Marten tenant here means one
gameserver, always inside a single deployment's single Postgres database,
never across deployments. The two concepts don't overlap (a docs-tenant's
database never holds more than one docs-tenant's data, so there's nothing
for Marten to partition at that layer). Recommend a short cross-reference
added to §4.1 pointing at this doc so a future reader doesn't conflate them.

## Current state (for reference)

- **Already correct:** `Account` lookup is tenant-wide by Bohemia ID (one
  account per player across every gameserver). `GameServer` (keyed by
  `ClientId`) and `WhitelistApplication` are already explicitly scoped by
  `serverClientId` as a plain field/parameter, resolved from the caller's
  JWT `client_id` claim at the API layer (`AccountEndpoints.cs`), never
  trusted from the request body.
- **Missing:** `Character`, `Company`, `Bank`, `BankAccount` carry no server
  identifier anywhere — `CreateCharacterCommand`, `CreateCompanyCommand`,
  `OpenBankCommand`, `OpenBankAccountCommand` have no `ServerClientId`/
  `GameServerId` field, and several Banking read endpoints (`GET banks`,
  `GET characters/{id}/bank-accounts`, `GET companies/{id}/bank-accounts`,
  `GET bank-accounts/{id}`, `GET bank-accounts/{id}/transactions`) use bare
  `.RequireAuthorization()` with no scope restriction at all.
- **Confirmed:** every write endpoint across `Characters`, `Banking`, and
  `Companies` already requires a `gameserver:*` scope
  (`gameserver:characters:write`, `gameserver:banking:manage`,
  `gameserver:banking:write`, `gameserver:companies:write`) — only the
  Bridge calls these today, there is no separate admin/staff path for these
  three modules the way `Accounts` has `accounts:manage`/`admin`. So the
  gameserver's own `client_id` claim is sufficient to resolve tenancy for
  every current call site; no admin-on-behalf-of-a-server case exists yet.

## New abstraction: `ICurrentGameServer`

```csharp
public interface ICurrentGameServer
{
    string ClientId { get; }
}
```

- **Defined three times, once per module** —
  `Characters.Application.Common.ICurrentGameServer`,
  `Banking.Application.Common.ICurrentGameServer`,
  `Companies.Application.Common.ICurrentGameServer` — not as a shared type
  in `Shared.Kernel`. `Shared.Kernel` today holds only strongly-typed
  aggregate IDs (`AccountId`, `CharacterId`, `CompanyId`); modules don't
  share Application-layer ports with each other either — cross-module
  contact stays through Mediator `XxxLookupQuery`s only (§9e). Bending
  either convention to save a 3-line interface isn't worth it.
- **Production implementation**, one per module, in that module's
  `Infrastructure.Common`: `HttpContextCurrentGameServer(IHttpContextAccessor)`,
  reading `HttpContext.User.FindFirst("client_id")`. Registered via
  `TryAddScoped<ICurrentGameServer, HttpContextCurrentGameServer>()` inside
  the module's own `AddXInfrastructure`, alongside its repositories.
- **Fails closed:** throws on a missing/empty claim rather than falling
  back to an untenanted session — same posture as the existing "fail closed
  on malformed realm_access" fix. In practice this should be unreachable,
  since every endpoint that needs it already requires a `gameserver:*`
  scope, and Client Credentials tokens always populate `client_id`.
- **Host change:** `src/Api/Program.cs` needs one new line,
  `builder.Services.AddHttpContextAccessor()` — not currently registered.
- **Test doubles:** each integration test project's composition root
  (`TestServices.BuildProvider()` for `Banking.IntegrationTests`, each
  project's own `InitializeAsync` for `Characters.IntegrationTests` /
  `Companies.IntegrationTests`) registers a trivial fixed-`ClientId` fake
  per module *before* calling `AddXInfrastructure` — the module's own
  `TryAddScoped` then no-ops, same override seam already used for other
  test doubles in this codebase.

## Characters

- `MartenCharacterRepository`'s constructor takes `ICurrentGameServer`
  alongside `ICharactersStore`, and opens
  `store.LightweightSession(currentGameServer.ClientId)` instead of the
  bare `store.LightweightSession()`.
- `CharacterInfrastructureExtensions.AddCharacterInfrastructure` turns on
  tenancy for the store — event stream tenancy plus the `Character`
  document mapping (exact Marten API surface, e.g.
  `options.Events.TenancyStyle = TenancyStyle.Conjoined` and
  `options.Schema.For<Character>().MultiTenanted()`, to be confirmed against
  the installed Marten version during implementation).
- No change to the `Character` aggregate, `CreateCharacterCommand`, or
  `CharactersQuery` — tenancy is transparent to application/domain code
  once the session is opened correctly.

## Banking

- `MartenBankRepository` and `MartenBankAccountRepository` both take
  `ICurrentGameServer`, same session-opening change.
- Both `Bank` and `BankAccount` become multi-tenanted document/stream
  types. A `Bank` opened by one gameserver becomes invisible to another
  (matches the "isolated per server" decision); a `BankAccount` inherits
  isolation the same way, since it's always opened against a `Bank` and a
  `Character` resolved within the same tenant-scoped session.
- No command/query signature changes in `Banking.Application`.

## Companies

- `MartenCompanyRepository` takes `ICurrentGameServer`, same change.
- `Company` becomes multi-tenanted.
- No command/query signature changes in `Companies.Application`.

## Accounts / GameServer — unchanged

`Account`, `GameServer`, and `WhitelistApplication` keep their current
untenanted Marten config and their existing explicit
`serverClientId`-as-parameter pattern. `Account` is genuinely tenant-wide by
design (one player, one account, reachable from every gameserver);
`GameServer`/`WhitelistApplication` are already correctly scoped by an
explicit `clientId` field and don't need Marten-level partitioning on top
of that. No reason to touch working, already-tested code.

## Data flow

`ICurrentGameServer` resolves exactly one way, for every current call site:
read `client_id` off the caller's JWT, same claim `session-bootstrap`
already trusts. Each of the three repositories takes it as a constructor
dependency; since repositories are scoped and constructed once per request
scope, the session is opened correctly-tenanted exactly once per request.
Cross-module calls (e.g. `Companies` calling `CharacterLookupQuery`, or
`Banking` calling it too) stay consistent automatically, because both
sides' repositories resolve the same `ICurrentGameServer` value from the
same request scope — there's no way for one module to end up reading a
different gameserver's partition than another module in the same request.

## Error handling

- **Cross-server references need no new error type.** If a character
  resolved within server A's tenant-scoped session tries to reference a
  `BankId` that only exists under server B's partition, Marten's
  `FindByIdAsync`/`LoadAsync` simply returns `null` within that tenant scope
  — which already flows into the existing `BankNotFound`/`CharacterNotFound`
  /`FounderNotFound` union cases every affected handler already returns.
  "Wrong server" and "doesn't exist" become indistinguishable by
  construction, which is the right behavior across a trust boundary.
- **Missing/empty `client_id` claim:** `HttpContextCurrentGameServer` throws.
  Today this surfaces as an unhandled 500. Given every endpoint that needs
  it already requires a `gameserver:*` scope (see "Current state" above),
  this should be unreachable in practice — flagged as an open question
  below rather than built out now, to avoid handling a case with no known
  trigger.

## Testing

- **Unit tests** (`*.Domain.UnitTests`): unaffected — they exercise
  aggregates in isolation, no Marten session involved.
- **Integration tests:** each composition root (`Banking.IntegrationTests/
  TestServices.cs`, `Characters.IntegrationTests`'s and
  `Companies.IntegrationTests`'s own `InitializeAsync`) registers a fixed-
  `ClientId` fake per module, as described above.
- **New tests, one per affected module** (Characters, Banking, Companies):
  build two scopes off two providers configured with different fake
  `ClientId`s, create an entity under one, and assert it's invisible
  (`NotFound`/empty list) from the other. This is the actual regression
  proof that tenancy partitioning is wired correctly, not just declared in
  config.
- **Existing tests:** unaffected, as long as each test class consistently
  uses one fake `ClientId` throughout — matches what's already implicitly
  true today (e.g. `CreateSessionCommand(bohemiaId, "gameserver-dev")` is
  already hardcoded throughout the existing suites).

## Local dev rollout

Turning on conjoined tenancy changes the underlying schema shape for the
`characters`, `banking`, and `companies` schemas' event and document tables.
There's no production deployment and no CI yet, so the local fix is the
same one README already documents: `docker compose down -v` for a full
reset, or dropping just the three affected schemas directly (e.g. `DROP
SCHEMA IF EXISTS characters CASCADE;`) the same way the "Resetting local
data" section already shows for `account`.

## Out of scope

- **An admin/staff cross-server view** for Characters/Banking/Companies —
  nothing calls these modules with anything but a `gameserver:*`-scoped
  token today. Adding one later would need a genuinely new admin scope plus
  an `ICurrentGameServer` variant that resolves from an explicit route
  parameter instead of a claim (and, for reporting across every server,
  Marten's own cross-tenant query support). Deferred until an actual admin
  UI need exists for these three modules.
- **Promoting `GameServer.ClientId` (string) to a proper `GameServerId`
  (GUID) value object** in `Shared.Kernel` — would decouple domain identity
  from OAuth client naming/rotation, but the tenant id Marten needs is a
  string either way (`ClientId` itself), so this is a separate, independent
  cleanup, not a prerequisite for this change.
- **Mapping the fail-closed 500 above to a proper `ProblemDetails` response**
  — deferred until/unless it's shown to be reachable in practice.
