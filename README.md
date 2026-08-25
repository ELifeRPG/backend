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

Keycloak runs a **custom image** carrying two provider jars — the realm declares the
`link-bohemia-gameaccount` required action and the backend calls this realm's
`/bohemia-gameaccount/pin` endpoint, neither of which exists on stock Keycloak, and the `eliferpg`
theme styles the account/admin/email pages.
[infra/keycloak/Dockerfile](./infra/keycloak/Dockerfile) composes it: a stock Keycloak server plus
one `COPY` per provider jar, each lifted from that plugin's published image at a pinned version.
Compose builds it for you — nothing to build by hand.

```sh
docker compose up -d
```

Adding another provider is an `ARG`, a `FROM`, and a `COPY` in that Dockerfile; bumping one is a
single version change there. Because Compose only builds when the image is missing, re-run with
`docker compose up -d --build` after either.

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

- `gameserver-dev` — stands in for one gameserver's Bridge client (Client Credentials, `gameserver:session:create` + `gameserver:characters:write` + `gameserver:banking:manage` + `gameserver:banking:write` + `gameserver:companies:write` + `gameserver:skills:write` scopes, `impersonation` role). Secret: `local-dev-only-not-a-real-secret`.
- `loginTheme` is **`eliferpg-reforger`**, not `eliferpg`, even though this image now
  layers both provider jars. `eliferpg-reforger` (from keycloak-bohemia-gameaccount) is
  the only theme here that carries `link-bohemia-gameaccount.ftl`, and
  `keycloak-theme-eliferpg` 1.0.1's `eliferpg` login theme declares `parent=keycloak`, so
  it neither ships that template nor inherits it. Pointing `loginTheme` at `eliferpg`
  makes the required-action page fail with a FreeMarker `TemplateNotFoundException`
  (HTTP 500). The other three theme fields stay on `eliferpg` — account, admin, and email
  need no template from the provider. Once `eliferpg`'s login theme reparents onto
  `eliferpg-reforger` (or ships the template itself), `loginTheme` can move back to
  `eliferpg` and the login page gets the styled theme too.
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

**Rollout note for existing local Postgres volumes (hive migration):** this change rebuilds five modules' doc-table primary keys and changes `mt_doc_gameserver`'s identity from `varchar(clientId)` to `uuid(Id)` — a table rebuild Marten cannot do in place against an existing schema. If you have a running Postgres volume from before this change, wipe it (`docker compose down -v`, see "Resetting local data" below) so the next `docker compose up -d` starts from a clean schema. Afterward, a gameserver must be registered via `POST /api/game-servers` (see [docs/accounts.md](./docs/accounts.md#game-server-registry)) before character or shop creation will work.

### Keycloak providers (theme + bohemia-gameaccount)

The `keycloak` service's image combines two provider jars, each pulled from its
own published `ghcr.io` image via a multi-stage `infra/keycloak/Dockerfile`:

```dockerfile
FROM ghcr.io/eliferpg/keycloak-theme-eliferpg:1.0.1 AS theme
FROM ghcr.io/eliferpg/keycloak-bohemia-gameaccount:1.0.0 AS bohemia

FROM quay.io/keycloak/keycloak:26.0
COPY --from=theme /opt/keycloak/providers/ /opt/keycloak/providers/
COPY --from=bohemia /opt/keycloak/providers/ /opt/keycloak/providers/
```

`docker compose up -d` (which builds the `keycloak` service by default) is all
that's needed — there's nothing to fetch separately. To pick up a newer release
of either one, bump that stage's version tag in `infra/keycloak/Dockerfile`, then
`docker compose build keycloak` (or `docker compose up -d --build keycloak`).

**[`eliferpg` theme](https://github.com/ELifeRPG/keycloak-theme-eliferpg)** —
ships login, account, admin, and email variants, configured via
`loginTheme`/`accountTheme`/`adminTheme`/`emailTheme` in
`infra/keycloak/eliferpg-realm.json`. This realm uses it for all but `loginTheme`, which
stays on `eliferpg-reforger` — see the realm notes above for why.

**[`keycloak-bohemia-gameaccount`](https://github.com/ELifeRPG/keycloak-bohemia-gameaccount)**
— a required action, registered in `eliferpg-realm.json`'s `requiredActions` as
alias `link-bohemia-gameaccount` (`defaultAction: false`). It's
**application-initiated only** — nothing happens by default. A client triggers it
by adding `kc_action=link-bohemia-gameaccount` to the authorization request;
Keycloak reports back `kc_action_status=success` on the redirect. See that repo's
README for how it's meant to be triggered and how to consume the resulting
`bohemiaId` user attribute (e.g. via a stock `oidc-usermodel-attribute-mapper`).

Same rollout caveat as above applies to both the theme fields and the required
action: they only take effect on a fresh realm import. Against an existing
volume, either reset it (`docker compose down -v`) or apply them manually to the
running realm — the theme fields via the Admin API/console (Realm Settings →
Themes), and the required action via its two-step Admin API sequence (`POST
.../authentication/register-required-action` then `PUT
.../authentication/required-actions/link-bohemia-gameaccount` — the PUT alone
first 404s).

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
- [docs/skills.md](./docs/skills.md) — character skill progression
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

Exercises `CreateSessionCommand` against **live** Postgres and Keycloak — requires the local infra
stack running and the devcontainer connected to its network, same as the manual steps above. Not yet
wired into any CI, since none exists in this repo yet.

Most tests no longer touch Keycloak at all: accounts are created by portal signup, so
`TestAccounts.CreateAsync` builds them straight from the aggregate. The exceptions are the ones that
assert on Keycloak-side effects — lock/unlock disabling a user, realm-role assignment, and the
unlinked-join case, which mints a real PIN through the linking SPI. Those create their Keycloak users
through `KeycloakTestClient` and clean them up in teardown.

To run these **from the host** instead of the devcontainer, point them at the mapped ports:

```sh
ELIFERPG_TEST_DB="Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=supersecret" \
ELIFERPG_TEST_KEYCLOAK_URL="http://localhost:8180/" \
  dotnet test tests/Accounts.IntegrationTests/Accounts.IntegrationTests.csproj
```

Run the full solution's tests with `-m:1`. Every project shares one Postgres, and running the
projects in parallel lets them interfere — a parallel run has been observed failing a handful of
tests that pass individually.

The `Characters` module has the same two-project split (`tests/Characters.Domain.UnitTests`, `tests/Characters.IntegrationTests`) — the latter also exercises the cross-module `AccountLookupQuery` call into `Accounts.Application`, so it needs both modules' infrastructure wired up, not just `Characters`'.

`Banking` follows the same split (`tests/Banking.Domain.UnitTests`, `tests/Banking.IntegrationTests`). The integration project needs `Accounts`, `Characters`, `Companies`, *and* `Banking` infrastructure wired up — opening a personal bank account depends on `CharacterLookupQuery`, a Corporate account's withdraw/transfer authorization depends on `CompanyMemberPermissionsQuery`, and a character in turn depends on an account. It has two test classes (`BankingCommandTests`, `CorporateBankAccountTests`) sharing one `TestServices.BuildProvider()` factory rather than each doing its own DI setup — `Mediator.SourceGenerator` only allows a single `AddMediator(...)` call site per compiled test project, so a second class with its own call fails to build even with an identical assembly list.

`Companies` follows the same split too (`tests/Companies.Domain.UnitTests`, `tests/Companies.IntegrationTests`), reusing the same `CharacterLookupQuery` `Banking` already needed — no new cross-module plumbing was required for this module.

Run everything at once (all eight test projects, unit and integration) with:

```sh
dotnet test ELifeRPG.Core.slnx
```

### Discord login

`infra/keycloak/add-discord-idp.sh` adds Discord as an identity provider:

```sh
DISCORD_CLIENT_ID=... DISCORD_CLIENT_SECRET=... ./infra/keycloak/add-discord-idp.sh
```

It applies to a **running** Keycloak over the Admin API rather than editing
`eliferpg-realm.json`, on purpose. Verified against 26.0.8: a realm referencing a
`providerId` the server does not have makes Keycloak refuse to start at all (`Invalid
identity provider id`), so committing a Discord block before the provider is in the image
would take the whole stack down, not just Discord login. Over the Admin API the same
mistake is a 4xx from the script.

**Discord needs a provider extension; stock Keycloak cannot broker it.** There is no
Discord provider in 26.0.8, and the generic `oidc` one does not work — verified end to
end against a stand-in Discord, three separate failures: no `id_token` in the token
response (Discord is OAuth2, not OIDC), `id` instead of `sub` in `/users/@me`, and OIDC
nonce validation. The script refuses up front and explains this rather than creating a
provider nobody can log in through. Once a Discord provider jar is in the image, re-run
with `--provider-id <that id>`.

`--print-realm-json` emits the block for `eliferpg-realm.json` once that is true. It uses
`${DISCORD_CLIENT_ID}`/`${DISCORD_CLIENT_SECRET}` placeholders, which Keycloak substitutes
from the environment at import time (verified), so the secret stays out of git.

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
