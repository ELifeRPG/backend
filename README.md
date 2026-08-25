# ELifeRPG Core

External infrastructure for the ELifeRPG ArmA Reforger mod — Central API, authentication, and event processing. This repo is a from-scratch rewrite of [ELifeRPG/Core](https://github.com/ELifeRPG/Core); see [ARCHITECTURE.md](./ARCHITECTURE.md) for the target design, [MIGRATION.md](./MIGRATION.md) for the legacy-app analysis and phase-by-phase migration plan, and [docs/](./docs) for feature-by-feature usage (one file per module — [accounts](./docs/accounts.md), [characters](./docs/characters.md), [banking](./docs/banking.md), [companies](./docs/companies.md), [items](./docs/items.md), [shops](./docs/shops.md)). The Bridge Service lives in its own repo, [eliferpg-reforger-bridge](../eliferpg-reforger-bridge).

**Status:** All four originally-planned modules (Accounts/Sessions, Characters, Banking, Companies), Banking's Corporate bank accounts, and two new modules added afterward — Items (a staff-curated catalog) and Shops (Character-/Company-owned shops selling catalog items, settled via Banking, with a SignalR hub for live listing updates — the first real-time feature in the codebase) — are complete and verified. See [MIGRATION.md §5](./MIGRATION.md#5-migration-plan-feature-1--accounts--sessions), [§6](./MIGRATION.md#6-migration-plan-feature-2--characters), [§7](./MIGRATION.md#7-migration-plan-feature-3--banking), [§8](./MIGRATION.md#8-migration-plan-feature-4--companies), and [§9](./MIGRATION.md#9-corporate-bank-accounts-banking--companies) for the full phase-by-phase breakdown. The `Accounts`, `Characters`, `Banking`, and `Companies` modules, the `src/Api` host, the Kiota-generated `Bridge.ApiClient`, and a minimal `Bridge.Host` are all built and verified end-to-end — a local call through the Bridge provisions an account and a real Keycloak user via the Central API, then exchanges for a genuine player-impersonating JWT, all against live Postgres/Keycloak; `POST /api/characters`/`GET /api/accounts/{accountId}/characters`, the full Banking flow (open bank, open personal or corporate account, deposit, withdraw, transfer between accounts), and the full Companies flow (create a company, add members, duplicate-member guard) are all verified through the real running host too — including a Corporate account's authorization actually being enforced (a company's "Owner" position can withdraw, a "Rookie" member cannot). Unit and integration tests exist and pass against real infra. OpenTelemetry is wired into `src/Api` (confirmed reaching Tempo/Prometheus) but not yet into `Bridge.Host`, and there's no CI pipeline yet. Next up: `Companies` position management (custom positions beyond the seeded Owner/Rookie), broader `CompanyPermissions` enforcement beyond `ManageFinances`/`ManageShops`, or moving toward CI.

## Prerequisites

- Docker
- VS Code with the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension, **or** the [devcontainer CLI](https://github.com/devcontainers/cli) (`npm install -g @devcontainers/cli`)

You do **not** need .NET installed on your host — the devcontainer provides the pinned **.NET 11 preview** SDK (see [global.json](./global.json)). All `dotnet` commands below assume you're running inside the devcontainer.

## Getting started

### Open the devcontainer

- VS Code: **Reopen in Container**.
- CLI: `devcontainer up --workspace-folder .`, then run commands with `devcontainer exec --workspace-folder . <command>`.

### Start local infrastructure

```sh
docker compose up -d
```

This starts Postgres, Keycloak, and the observability stack (OpenTelemetry Collector, Tempo, Prometheus, Loki, Grafana). Host ports are intentionally **not** the defaults, since those are commonly already taken by other local services:

| Service | Container port | Host port | Notes |
|---|---|---|---|
| Postgres | 5432 | **5433** | user `postgres`, password `supersecret` |
| Keycloak | 8080 | **8180** | admin console login `admin` / `admin` |
| Grafana | 3000 | 3000 | anonymous access enabled, Admin role, for local dev only |
| Prometheus | 9090 | **9091** | |
| Loki | 3100 | **3101** | |
| Tempo | 3200 | 3200 | |
| OTel Collector (OTLP gRPC / HTTP) | 4317 / 4318 | **4327** / **4328** | |

If you're running this on a machine with nothing else on those ports, feel free to adjust `compose.yml` back to the defaults — just keep it consistent for everyone working in the repo.

**If you're working from inside the devcontainer** (which is where you'll actually run `dotnet build`/`dotnet run`), connect it to the same Docker network so it can resolve the other containers by service name instead of `localhost`:

```sh
docker network connect eliferpg-core_core <devcontainer-container-name>
```

Inside the devcontainer, Postgres is then reachable at `postgres:5432` and Keycloak at `keycloak:8080` — not via the host-mapped ports above, which are for tools running on your host machine (a browser, `psql`, etc.).

### Keycloak realm

The `eliferpg` realm (this local instance's one dev tenant — see [ARCHITECTURE.md §4.1](./ARCHITECTURE.md#41-identity-provider-keycloak) for the one-realm-per-tenant model) is auto-imported on container start from [infra/keycloak/eliferpg-realm.json](./infra/keycloak/eliferpg-realm.json). It comes preconfigured with:

- `gameserver-dev` — stands in for one gameserver's Bridge client (Client Credentials, `gameserver:session:create` + `gameserver:characters:write` + `gameserver:banking:manage` + `gameserver:banking:write` + `gameserver:companies:write` scopes, `impersonation` role). Secret: `dev-secret-change-me`.
- `account-service` — used to provision Keycloak users for new accounts (Client Credentials, `manage-users` + `view-realm` roles on `realm-management`). Secret: `account-service-secret`.

These are throwaway local-dev values, intentionally committed in plaintext — same posture as the Postgres/Grafana/Keycloak-admin credentials above. If you change anything in the realm via the admin console and want to persist it, re-export and patch the client secrets back in (Keycloak redacts them on export):

```sh
TOKEN=$(curl -s -X POST http://localhost:8180/realms/master/protocol/openid-connect/token \
  -d "client_id=admin-cli" -d "username=admin" -d "password=admin" -d "grant_type=password" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -s "http://localhost:8180/admin/realms/eliferpg/partial-export?exportClients=true&exportGroupsAndRoles=true" \
  -H "Authorization: Bearer $TOKEN" -X POST -H "Content-Type: application/json" -d '{}' \
  > infra/keycloak/eliferpg-realm.json

# then manually restore the "secret" field for gameserver-dev / account-service in the exported file
```

**Rollout note for existing local Keycloak volumes:** the `view-realm` grant on `account-service` and the `admin` realm role (added for account role management, see [docs/accounts.md](./docs/accounts.md#managing-an-accounts-roles)) are baked into `infra/keycloak/eliferpg-realm.json` and only take effect on a fresh `--import-realm` — i.e. a new or reset Keycloak container/volume. If you already have a running Keycloak container from before this change, its realm was imported once and won't pick these up automatically. Either wipe it (`docker compose down -v`, see "Resetting local data" below) so the next `docker compose up -d` re-imports the updated realm, or apply the grants manually via the Admin API against your existing container. Otherwise the new role-management endpoints will 403.

### Keycloak theme

The `eliferpg` realm is configured to use the [`eliferpg` Keycloak theme](https://github.com/ELifeRPG/keycloak-theme-eliferpg) (login, account, admin, and email) via `loginTheme`/`accountTheme`/`adminTheme`/`emailTheme` in `infra/keycloak/eliferpg-realm.json`. The theme is baked into the `keycloak` service's image at build time: `infra/keycloak/Dockerfile` is `FROM ghcr.io/eliferpg/keycloak-theme-eliferpg:<version>` — that upstream image already has the theme jar in `/opt/keycloak/providers/`, so `docker compose up -d` (which builds the `keycloak` service by default) is all that's needed; there's nothing to fetch separately.

To pick up a newer theme release, bump the version tag in `infra/keycloak/Dockerfile`, then `docker compose build keycloak` (or `docker compose up -d --build keycloak`).

Same rollout caveat as above applies to the theme fields: they only take effect on a fresh realm import. Against an existing volume, either reset it (`docker compose down -v`) or set the four theme fields on the running realm via the Admin API/console (Realm Settings → Themes).

### Build

```sh
dotnet build
```

### Run

```sh
dotnet run --project src/Api/Api.csproj
```

Listens on `http://localhost:5100`. In `Development` (the default via `launchSettings.json`), it serves an OpenAPI doc at `/openapi/v1.json` and interactive docs at `/docs`.

For feature-by-feature `curl` walkthroughs (minting a Bridge token, and exercising each module's endpoints), see:

- [docs/accounts.md](./docs/accounts.md) — session bootstrap
- [docs/characters.md](./docs/characters.md) — characters and their sessions
- [docs/banking.md](./docs/banking.md) — banks, accounts, deposits/withdrawals/transfers, Corporate accounts, transaction history
- [docs/companies.md](./docs/companies.md) — companies and membership
- [docs/items.md](./docs/items.md) — the staff-curated item catalog
- [docs/shops.md](./docs/shops.md) — Personal/Corporate shops, listings, purchasing, and live listing updates

### Run the Bridge

The Bridge Service now lives in its own repo, [eliferpg-reforger-bridge](../eliferpg-reforger-bridge) — see its README for how to run it and regenerate its Kiota-generated API client against this repo's `src/Api`.

### Test

```sh
dotnet test tests/Accounts.Domain.UnitTests/Accounts.Domain.UnitTests.csproj
```

Pure unit tests for the `Account` aggregate's invariants (`Lock`/`Unlock`, event replay) — no infrastructure required.

```sh
dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj
```

Exercises `CreateSessionCommand` against **live** Postgres and Keycloak (creates a real Keycloak user, cleans it up in teardown) — requires the local infra stack running and the devcontainer connected to its network, same as the manual steps above. Not yet wired into any CI, since none exists in this repo yet.

The `Characters` module has the same two-project split (`tests/Characters.Domain.UnitTests`, `tests/Characters.IntegrationTests`) — the latter also exercises the cross-module `AccountLookupQuery` call into `Accounts.Application`, so it needs both modules' infrastructure wired up, not just `Characters`'.

`Banking` follows the same split (`tests/Banking.Domain.UnitTests`, `tests/Banking.IntegrationTests`). The integration project needs `Accounts`, `Characters`, `Companies`, *and* `Banking` infrastructure wired up — opening a personal bank account depends on `CharacterLookupQuery`, a Corporate account's withdraw/transfer authorization depends on `CompanyMemberPermissionsQuery`, and a character in turn depends on an account. It has two test classes (`BankingCommandTests`, `CorporateBankAccountTests`) sharing one `TestServices.BuildProvider()` factory rather than each doing its own DI setup — `Mediator.SourceGenerator` only allows a single `AddMediator(...)` call site per compiled test project, so a second class with its own call fails to build even with an identical assembly list.

`Companies` follows the same split too (`tests/Companies.Domain.UnitTests`, `tests/Companies.IntegrationTests`), reusing the same `CharacterLookupQuery` `Banking` already needed — no new cross-module plumbing was required for this module.

Run everything at once (all eight test projects, unit and integration) with:

```sh
dotnet test ELifeRPG.Core.slnx
```

### Local secrets

Local-dev values (the Keycloak client secrets and Postgres password above, already in `appsettings.Development.json`) are intentionally committed — they're throwaway and only ever used against the local Compose stack, never a real deployment, the same way the legacy app committed its own local `supersecret` Postgres password.

Anything environment-specific or genuinely sensitive — a non-dev Keycloak client secret, a future third-party OAuth secret for account linking (see [ARCHITECTURE.md §8](./ARCHITECTURE.md#8-future-considerations)), a connection string override — should go through `dotnet user-secrets` against the `src/Api` host, not into `appsettings.json`:

```sh
dotnet user-secrets init --project src/Api/Api.csproj
dotnet user-secrets set "Keycloak:ProvisioningClientSecret" "your-value" --project src/Api/Api.csproj
```

This mirrors the legacy app's approach (one shared `UserSecretsId` on its `Migrator` project, since every app read from the same secrets store) — here the equivalent anchor project is the host, since that's where configuration actually gets bound at runtime for every module.

### Resetting local data

Wipe everything (Postgres data, including Keycloak's own tables — it's backed by the same Postgres instance under its own `keycloak` schema):

```sh
docker compose down -v
```

To reset just one module's event store without touching anything else, drop its schema directly:

```sh
docker exec eliferpg-core-postgres-1 psql -U postgres -c "DROP SCHEMA IF EXISTS account CASCADE;"
```

The same works for any other module's schema — `characters`, `banking`, `companies` — if you only need to reset one.

### Observability

Grafana at [http://localhost:3000](http://localhost:3000) (anonymous admin access, local dev only) has Prometheus, Loki, and Tempo pre-provisioned as datasources — see [ARCHITECTURE.md §9d](./ARCHITECTURE.md#9d-observability).

## Solution structure

One deployable process organized as a **modulith** — each bounded context gets its own `Domain`/`Application`/`Infrastructure`/`Api` projects, composed by a thin host. See [ARCHITECTURE.md §9e](./ARCHITECTURE.md#9e-modulith-structure--module-boundaries) for the full layout and the naming/dependency rules, and [MIGRATION.md](./MIGRATION.md) for why each of those decisions was made and what's actually built so far.
