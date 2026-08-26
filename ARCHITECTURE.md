# Architecture Specification: External Infrastructure for ArmA Reforger Custom Mod

**Status:** Draft
**Scope:** Backend systems outside the game client/server process itself — Central API, authentication, event processing, and downstream consumers.

---

## 1. Overview

The custom ArmA Reforger mod requires a backend that can persist and process game events, expose data to future consumers, and do this securely despite the mod's limited scripting environment and the game's platform-native identity model (Bohemia ID).

### 1.1 Goals

- A single **Central API** as the authoritative integration point for all consumers.
- Secure, standards-based authentication (**OAuth 2.0 / OIDC**) across all components.
- An event-driven core that scales from "a few events per minute" to high-volume gameplay telemetry without introducing unnecessary infrastructure up front.
- Support for future consumers without re-architecting: an **NPC Simulation Module** and an **Admin UI** are known up front; more will follow.
- Typed, generated API clients rather than hand-maintained HTTP wrappers, so consumers stay in sync with the Central API's contract automatically.
- Full request/log/trace correlation from day one, self-hosted, without a vendor SaaS dependency.

### 1.2 Non-Goals (for this iteration)

- A full external message broker (Kafka/RabbitMQ/NATS) is deferred until measured load justifies it.
- Player-facing account linking (Discord/Steam) is out of scope for v1; see [Section 8](#8-future-considerations).
- Kubernetes is not adopted in this iteration; the system is designed so it can migrate to Kubernetes later without a redesign (see [Section 9a](#9a-deployment-topology)).

---

## 2. Core Problem: Identity

The game server authenticates players via Bohemia's platform before they connect; the mod only has access to a **Bohemia ID** (a long string/GUID) per player. There is no way to run an interactive OAuth redirect flow inside the game client. Consequently:

- The Bohemia ID must be treated as a claim **asserted by a trusted server component**, never as a bearer credential in itself.
- OAuth 2.0 is used **between backend services**, not between the player and the backend directly.
- A **token exchange** step converts a server-asserted Bohemia ID into a short-lived, narrowly scoped access token that downstream services can validate like any other JWT.

---

## 3. System Components

| Component | Role | Auth Grant |
|---|---|---|
| ArmA Reforger Mod | Server-side script; knows the connected player's Bohemia ID | none (delegates to Bridge) |
| Bridge Service | Runs on the gameserver host; holds credentials, calls the Central API | Client Credentials (per gameserver instance) |
| Keycloak | OAuth 2.0 / OIDC provider | — |
| Central API | Resource server; validates tokens, owns business logic and event ingestion | — |
| PostgreSQL + Marten | Event store and read models | — |
| NPC Simulation Module | Consumes events, calls back into the API | Client Credentials |
| Admin UI | Human-operated management interface | Authorization Code + PKCE |
| Observability Stack | Collects logs/metrics/traces from all .NET components | — |

### 3.1 ArmA Reforger Mod

Server-side Enfusion script. Talks only to the local Bridge Service (localhost HTTP or named pipe), never directly to the Central API. This keeps secrets and HTTP resilience logic (retries, batching, offline buffering) out of the mod's sandbox and out of the PBO package.

### 3.2 Bridge Service

A .NET Worker Service running alongside the dedicated server process.

- Uses `IHttpClientFactory` + Polly for retries/circuit breaking.
- Calls the Central API through a **Kiota-generated C# client** (see [Section 9c](#9c-api-client-generation-kiota)) rather than hand-rolled HTTP calls, so its contract with the Central API stays in sync automatically.
- Calls Keycloak's token endpoint **directly** (not through the Central API) to exchange its own client-credentials token for a player-impersonating one, per the verified mechanics in [§4.3](#43-player-identity-token-exchange) — the Central API only tells it which Keycloak user to request via `requested_subject`.
- Buffers events locally (e.g. SQLite) if the Central API is temporarily unreachable, flushing once connectivity is restored.
- Batches outbound events into `POST /api/events/batch` rather than one call per event.
- Holds its own OAuth client credentials (**one client per gameserver instance**, not a shared secret across the fleet), so a compromised instance can be revoked individually.
- Lives in **its own repository**, `eliferpg-reforger-bridge` (see [Section 9b](#9b-repository--solution-structure)) — code-wise it was already fully decoupled from the Central API (HTTP + Kiota client only, no shared project references), so it now follows the same out-of-repo-consumer pattern as the Admin UI and NPC Simulation Module.

### 3.3 Central API

ASP.NET Core (.NET) resource server, structured internally as a **modulith** (modular monolith) — one deployable process, but with hard-enforced module boundaries so any module can be extracted into its own service later without a redesign. See [Section 9e](#9e-modulith-structure--module-boundaries) for the full project layout and boundary rules. Responsibilities:

- Validates bearer tokens issued by Keycloak (`Microsoft.AspNetCore.Authentication.JwtBearer`).
- Exposes the batch event ingestion endpoint.
- Owns the event store (via Marten, one store per module) and derived read models.
- Pushes live updates to subscribed consumers via SignalR.
- Exposes REST endpoints for catch-up/replay (`GET /events?since=<sequence>`).
- Publishes an OpenAPI document that is the source of truth for all Kiota-generated clients (Bridge, Admin UI, and any NPC Simulation Module client).

### 3.4 NPC Simulation Module & Admin UI

Both are external consumers of the Central API, added without changing the trust model:

- **NPC Simulation Module**: machine-to-machine, subscribes to relevant event types, may write back simulation results. Its runtime/language is **not yet decided** — event and API contracts are kept language-agnostic (JSON over HTTP/SignalR, standard OAuth 2.0/OIDC, OpenAPI-described endpoints) so the choice doesn't require Central API changes either way. If it ends up on .NET it can adopt the same Kiota-generated client as the Bridge; otherwise it consumes a Kiota client generated for its own language, or the raw OpenAPI spec directly.
- **Admin UI**: human-operated, standard browser-based OAuth login via Keycloak (Authorization Code + PKCE). Built as a **Vue SPA**, consuming a Kiota-generated TypeScript client against the Central API's OpenAPI spec. Lives in its own repository (see [Section 9b](#9b-repository--solution-structure)).

---

## 4. Authentication & Authorization

### 4.1 Identity Provider: Keycloak

Fully open source (Apache 2.0), self-hosted. Used as the single OAuth 2.0 / OIDC provider for all components.

**Tenancy:** one Keycloak realm per tenant, where a tenant is one self-hosted ELifeRPG deployment (its own gameserver fleet, Central API, Postgres, and Keycloak instance). A realm holds both that tenant's players and its staff/Admin UI accounts — see [§4.3](#43-player-identity-token-exchange) for why that's a safe boundary rather than a risk to split further. That per-deployment realm boundary is unchanged and is now the *only* tenancy boundary: one deployment is a single **hive** of game servers, where one server is one map, and all gameplay data — `Characters`, `Banking`, `Companies`, `Items`, and `Shops` — is hive-wide, reachable from every server in the deployment. This wasn't always true, and is worth stating precisely because it inverts a prior design: those five modules used to be further isolated *per gameserver instance* using a different, narrower sense of "tenant" — Marten's own conjoined multi-tenancy, keyed on the calling gameserver's OAuth client id, always inside this one deployment's single Postgres database — under which a character created via one gameserver was invisible from another, by construction. That isolation has been removed entirely (no `TenancyStyle.Conjoined`, no `AllDocumentsAreMultiTenanted()`, no per-tenant session, on any of the five modules). The guiding distinction: **tenancy exists to isolate; a hive needs to label.** Server association is now an explicit attribute rather than a partition — `Character.CurrentServerId` and `Shop.ServerId` record which server a character is currently on, or which server a shop physically stands on — backed by a `GameServer` registry entity (`Id`/`ClientId`/`DisplayName`/`MapName`, `POST`/`GET /api/game-servers`) that gives a server a durable `GameServerId` independent of its OAuth client id, so rotating or renaming the Keycloak client doesn't orphan the rows referencing it. See [docs/superpowers/specs/2026-08-22-hive-tenancy-design.md](docs/superpowers/specs/2026-08-22-hive-tenancy-design.md) for the full design; it supersedes [docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md](docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md), which is kept only for the rationale/verification history behind decisions that outlived it (notably §9e gotcha 9 below).

### 4.2 OAuth Clients

| Client | Grant Type | Scope examples | Notes |
|---|---|---|---|
| Bridge (per gameserver) | Client Credentials | `gameserver:events:write` | One client per server instance for independent revocation |
| NPC Simulation Module | Client Credentials | `npc:read`, `npc:write` | |
| Admin UI | Authorization Code + PKCE | role-based (admin, moderator, …) | Human login via Keycloak realm; Vue SPA using PKCE (no client secret in the browser) |
| Staff/Admin Tooling | Client Credentials, or Authorization Code + PKCE via `eliferpg-portal` | `accounts:manage` (scope); `admin` realm role | `accounts:manage` locks/unlocks (bans) an account and lists/searches accounts, checked as a client scope, never granted to a Bridge client. Granting/revoking a Keycloak realm role on an account (see [docs/accounts.md](./docs/accounts.md#managing-an-accounts-roles)) instead requires the `admin` realm role, checked the same `RealmRoleAuthorization` way as `whitelist-reviewer`/`server-admin` — genuinely role-conditional per Keycloak's `scopeMappings` (a client only sees a role in `realm_access` if both the user holds it and the client is scope-mapped to it), unlike a client scope. `eliferpg-portal` and `staff-admin-dev` both carry that `scopeMappings` entry, so a human staff member granted `admin` can use `eliferpg-webapp`'s "Roles" screen directly. |

### 4.3 Player Identity: Token Exchange

For requests made "on behalf of" a player, the Bridge exchanges the Bohemia ID for a short-lived, narrowly scoped access token (`sub=bohemia:<uid>`, e.g. `player:self` scope), modeled on OAuth 2.0 Token Exchange (RFC 8693).

- Only the Bridge (authenticated via its own client credentials) is permitted to perform this exchange — a raw Bohemia ID string must never be accepted as proof of identity on its own.
- Downstream services validate the resulting token like any standard JWT; they do not need to understand Bohemia-specific identity details.
- **Verified against Keycloak 26.0.8 (2026-08-14):** `TOKEN_EXCHANGE` ships disabled and must be turned on explicitly (`--features=token-exchange`). Once enabled, Keycloak's standard/V2 exchange only supports same-subject audience-narrowing — it flatly refuses `requested_subject` (`access_denied: Client not allowed to exchange`), so it cannot mint a token for an arbitrary Bohemia ID with no Keycloak account on its own. The working mechanism is the classic `impersonation` client role on the realm's `realm-management` client, granted to the Bridge's service-account user: with that role granted, `requested_subject=<username>` against an existing Keycloak user succeeds and returns a token whose `sub` is that user's real Keycloak id. This means **every player needs an actual Keycloak user record**. *(Superseded 2026-08-24: that record is no longer auto-provisioned by the Account module on first contact. Players sign up on the web portal — Discord broker or username/password — and bind their in-game identity afterwards by entering a PIN into Keycloak's own form; see the game-account linking flow below. A join by a Bohemia ID nobody has bound yet provisions nothing and returns `unlinked` plus that PIN.)*
- **Verified: the exchange call must be made by the Bridge itself, not proxied through the Central API.** Keycloak requires the client authenticating the token-exchange request to be the same client the `subject_token` was issued to — a second client (tested concretely: a `central-api` client without the `impersonation` role, presenting the Bridge's own access token as `subject_token`) is rejected with `access_denied: Client is not within the token audience`, a distinct failure from the missing-role case above. So the Central API **cannot** perform this exchange on a Bridge's behalf without possessing that Bridge's own client secret, which would defeat per-instance credential isolation (§4.2). The division of responsibility is therefore: the Central API's session-bootstrap endpoint (Account module) resolves the Account behind the Bohemia ID and returns its Keycloak **user id**; the **Bridge itself** then calls Keycloak's token endpoint directly, using its own client credentials, with `requested_subject=<keycloak user id>`. *(Updated 2026-08-24: this was a username while players had generated `bohemia_*` accounts. Portal-created users are named by the player, so the id is the only stable handle the Central API has. Verified against Keycloak 26.0.8 that `requested_subject` accepts a user id as well as a username, and that the resulting token carries the same `bohemia_id` claim either way.)* — exactly matching the bullet above ("only the Bridge... is permitted to perform this exchange"), now with the concrete mechanics behind it.
- **Tenancy model resolves the impersonation blast radius:** the `impersonation` role as granted is realm-wide, so it matters what else shares a realm with the players. The answer is **one Keycloak realm per tenant** — each self-hosted ELifeRPG deployment (its own gameserver fleet, its own Central API, its own Postgres, its own Keycloak instance/realm) is a separate tenant. A tenant's realm holds both its players and its staff/Admin UI accounts, and the Bridge's impersonation reach is scoped to that one realm — which is an acceptable trust boundary, since a Bridge already has broad authority over its own tenant's game state regardless (it's the sole writer of that tenant's event stream). A different tenant is a fully separate deployment, not a sibling realm on shared infrastructure, so cross-tenant impersonation isn't possible by construction. Per-instance revocability *within* a tenant is preserved as already specified in §3.2/§4.2: each gameserver instance for a tenant gets its own OAuth client id, not a shared one, so one compromised instance's credentials can still be revoked individually without affecting the tenant's other gameservers.
- **Verified: disabling a user (`enabled: false`) does NOT block a subsequent impersonation-based token-exchange for that user (2026-08-14).** Tested directly against a live Keycloak 26.0.8 instance: a `requested_subject` exchange for a disabled user still succeeds and returns a valid, usable access token — Keycloak's `enabled` flag is enforced for normal grants (a password-grant login against the same disabled user correctly fails with `Account disabled`) but not for this delegated-exchange path. Investigated closing this at the Keycloak layer (fine-grained admin permissions on the impersonate action, a bindable auth flow for token exchange, other realm/client config) — none apply. A follow-up pass confirmed a delegating `TokenExchangeProvider` SPI (claim the grant at higher `order()` rather than reimplementing it) is more tractable than initially estimated, but still means a permanent dependency on Keycloak's private SPI with no cross-version compatibility guarantee, plus a new JVM/Maven toolchain and a custom container image this repo has never needed — rejected on that basis, not deferred. Any feature that needs "this account can no longer act" to be enforced must check that condition in application code before calling this exchange — Keycloak's own account state is not an independent backstop here. `BridgeTokenProvider.ExchangeForPlayerTokenAsync` enforces this structurally (the status check is a required parameter the method itself gates on, not something each caller must remember), rather than relying on every call site independently — see `docs/superpowers/plans/2026-08-14-bridge-token-exchange-status-gate.md`.

### 4.4 Trust Boundary Principles

- No component other than the Bridge (and, for admins, the browser-based OIDC flow) may assert a player identity.
- Tokens issued via token exchange are short-lived and minimally scoped.
- Client secrets/certificates are rotated per gameserver instance; compromise of one instance does not compromise others.

---

## 5. Event Processing

### 5.1 Ingestion

- Bridge sends batched events to `POST /api/events/batch`.
- Each event carries a client-generated GUID for idempotency (deduplication on retry via a unique constraint).
- The Central API validates scope, schema, and per-server rate limits before persisting.

### 5.2 Storage: Marten on PostgreSQL

Marten (MIT licensed, fully open source) is used as the event store, avoiding the need for a dedicated event-store product or external broker:

- **Append-only event streams** per aggregate (e.g. per player, per server session).
- **Projections** derive read models from raw events (inline for cheap projections, async for more expensive ones).
- **Optimistic concurrency** per stream handles concurrent writes for the same player/server. This does not hold for the cross-module (`ICrossModuleTransaction`) write path, though: a `SessionOptions.ForTransaction`-bound session can't use Marten's version-checked append machinery at all, so that path instead uses a Postgres row lock (`SELECT ... FOR UPDATE`) — see §9e gotcha 9.
- **Subscriptions** provide the hook for pushing new events onward — this replaces a hand-built PostgreSQL `LISTEN`/`NOTIFY` listener.

### 5.3 Delivery to Consumers

- A Marten subscription feeds a **SignalR Hub**, which pushes live events to connected consumers (NPC Simulation Module, Admin UI).
- SignalR is a delivery convenience, not the source of truth: every consumer tracks the last processed sequence number and can catch up via `GET /events?since=<sequence>` after a disconnect or restart.

### 5.4 Retention

For high event volume, partition the underlying event tables by time (e.g. `pg_partman`) and define a retention/archival job to keep indexes performant.

---

## 6. Data Model (High-Level)

- **Players**: internal ID ↔ Bohemia ID mapping, created on first contact.
- **Event Streams**: keyed by aggregate (player, server session, etc.), containing the immutable event history.
- **Projections / Read Models**: derived, queryable current-state views generated by Marten.

---

## 7. Technology Stack Summary

| Concern | Technology | License |
|---|---|---|
| Central API | ASP.NET Core, **.NET 11 preview** (modulith — see §9e) | MIT |
| OAuth 2.0 / OIDC provider | Keycloak | Apache 2.0 |
| Event store | Marten (PostgreSQL), one store/schema per module | MIT |
| Database | PostgreSQL | PostgreSQL License |
| Real-time push | ASP.NET Core SignalR | MIT |
| Resilience (Bridge) | Polly | BSD-3 |
| Bridge runtime | .NET Worker Service | MIT |
| Admin UI | Vue | MIT |
| API client generation | Kiota | MIT |
| In-process messaging (CQRS) | `Mediator` (martinothamar, source-generated) — **not MediatR** | Apache 2.0 |
| Tagged unions / result types | .NET 11 preview native `union` declarations — **no `OneOf`**, verified working, see §9e | — |
| Strongly-typed IDs | `StronglyTypedId` | MIT |
| DTO↔domain mapping | Hand-written static `Dto.Create(source)` / `Dto.ToCommand()` — **no AutoMapper** | — |
| Local orchestration | Docker Compose | Apache 2.0 |
| Future orchestration | Kubernetes | Apache 2.0 |
| Tracing/metrics instrumentation | OpenTelemetry | Apache 2.0 |
| Metrics backend | Prometheus | Apache 2.0 |
| Log aggregation | Grafana Loki | AGPL-3.0 |
| Trace backend | Grafana Tempo | AGPL-3.0 |
| Observability dashboards | Grafana | AGPL-3.0 |

---

## 8. Future Considerations

- **External account linking** (Discord/Steam) for a companion web portal: use a device-authorization-grant-style flow — server displays a short code in-game chat, player enters it on a website and authenticates via OAuth there, backend links the accounts.
- **Message broker upgrade path**: if measured event volume exceeds what a single-node PostgreSQL/Marten setup can sustain, the Marten subscription can be replaced by a broker producer without changing the API or Bridge contracts.
- **Additional consumers**: any new consumer follows the same pattern — its own OAuth client, its own scopes, and either SignalR subscription or REST catch-up against the Central API.

---

## 9a. Deployment Topology

- **Now:** everything runs locally via **Docker Compose** — Central API, Bridge (for local testing against a dev gameserver), Keycloak, PostgreSQL, and the observability stack (Grafana, Loki, Tempo, Prometheus). One `compose.yml` (plus per-component override files) is the single source of truth for local environments — the devcontainer consumes it directly rather than duplicating it, listing it first in `dockerComposeFile` so the workspace container joins the same `core` network as the infra services (see the README's "Open the devcontainer").
- **Later:** **Kubernetes** is the anticipated next step once there's a real fleet of gameserver hosts and/or a need for independent scaling of the Central API, SignalR fan-out, and the observability stack. To keep that migration low-friction:
  - Components are configured entirely via environment variables / mounted config, never hardcoded host paths — this maps directly onto ConfigMaps/Secrets later.
  - Each component (Central API, Keycloak, Postgres, Grafana stack) is a separate container/service in Compose from day one, mirroring how they'd be separate Deployments/StatefulSets in k8s — avoid bundling multiple concerns into one container.
  - The Bridge Service is explicitly **not** containerized alongside the gameserver host's orchestration story — it runs as a plain process/Worker Service next to the dedicated server binary, since gameserver hosts are unlikely to be Kubernetes nodes themselves.

## 9b. Repository & Solution Structure

Four repositories, matching independent release cadence and consumer boundaries:

| Repo | Contents | Notes |
|---|---|---|
| `eliferpg-core` (this repo) | Central API (as a modulith, see §9e), shared contracts, Compose files, Marten/Postgres migrations | One `.sln`. |
| `eliferpg-reforger-bridge` | Bridge Service | Independent deploy cadence from the API (runs on gameserver hosts, not with the rest of the fleet); consumes the API only through the generated C# (Kiota) client, fetching the OpenAPI spec live from a running Central API instance rather than sharing a build with it. |
| Admin UI repo | Vue SPA | Independent deploy cadence from the API; consumes the API only through the generated TypeScript client. |
| NPC Simulation Module repo | NPC module (runtime TBD) | Independent deploy cadence; consumes the API only through a generated client (or raw OpenAPI) in whatever language is eventually chosen. |
| *(implicit)* Keycloak realm config | Realm/client/role definitions, exported as JSON | Track this as versioned config (either in `eliferpg-core` or its own small repo) rather than only living in the running Keycloak instance, so realm setup is reproducible. |

## 9c. API Client Generation (Kiota)

The Central API's OpenAPI document is the single contract definition; **Kiota** generates typed clients from it for every out-of-repo consumer:

- **Bridge Service** (out-of-repo, in `eliferpg-reforger-bridge`, consuming the Central API over HTTP like any other client): C# client generated via Kiota, regenerated by fetching the OpenAPI spec live from a running Central API instance (`GET /openapi/v1.json`) rather than from a shared build artifact, since the two repos don't share a build. **Built and verified:** `src/Bridge.ApiClient` in that repo (its `scripts/generate-bridge-client.sh` regenerates it), confirmed making a real authenticated call against the live host and correctly deserializing the response. Within `eliferpg-core`, `openapi/eliferpg-api-v1.json` is generated deterministically at `dotnet build` time by `src/Api` (via `Microsoft.Extensions.ApiDescription.Server`, see its `.csproj`) and also served live via `app.MapOpenApi()` in Development — this only works because the devcontainer sets `ASPNETCORE_ENVIRONMENT=Development` (see `.devcontainer/compose.yml`), which the build-time host needs to resolve Marten's Postgres connection string from `appsettings.Development.json`. CI wiring isn't set up yet in either repo — no CI pipeline exists in `eliferpg-core` at all yet, and cross-repo drift detection (e.g. `eliferpg-reforger-bridge`'s CI regenerating against a deployed Core instance and diffing) is future work. One environment note: the Kiota CLI targets a stable .NET runtime, not the preview one pinned for this solution, so the devcontainer installs both side by side (see [MIGRATION.md §5.1 step 5](./MIGRATION.md#51-steps)) — `eliferpg-reforger-bridge` needs the same side-by-side setup.
- **Admin UI**: TypeScript client generated via Kiota against the same OpenAPI spec, consumed by the Vue app.
- **NPC Simulation Module**: generates a Kiota client in whichever language it ends up using (Kiota supports C#, TypeScript, Python, Java, Go, PHP, Ruby, Swift), or falls back to consuming the OpenAPI spec directly if Kiota doesn't support the chosen language well.
- The OpenAPI spec itself should be published as a build artifact (not just served at runtime) so downstream repos can pin/regenerate clients against a specific Central API version rather than always generating against a live, possibly-ahead endpoint.

## 9d. Observability

OpenTelemetry instrumentation is added to the Central API and Bridge Service from the start (not bolted on later), exporting to a self-hosted **Grafana LGTM-style stack**:

- **Traces** → Grafana **Tempo**, correlating a request from Bridge → Central API → Marten/Postgres call, and onward through SignalR delivery to consumers.
- **Metrics** → **Prometheus**, scraping OTel-exported metrics (request rates/latencies, event ingestion throughput, batch sizes, Marten projection lag, SignalR connection counts).
- **Logs** → **Grafana Loki**, structured logs (Serilog or `Microsoft.Extensions.Logging` with an OTel exporter) correlated to traces via trace/span IDs in log context.
- **Grafana** ties the three together for dashboards and correlated drill-down (log line → trace → metric spike).
- All four (Grafana, Loki, Tempo, Prometheus) run as additional services in the local Docker Compose setup; an **OpenTelemetry Collector** sits in front of them so instrumentation in the .NET services only needs to know about a single OTLP endpoint, not the individual backends — this also keeps the migration path to Kubernetes or a future managed backend a config change rather than a code change.

## 9e. Modulith Structure & Module Boundaries

The Central API is one deployable process organized as a **modulith**: each bounded context (`Account`, `Banking`, `Characters`, `Companies`, …) gets its own vertical slice through Domain → Application → Infrastructure → **Api**, and a separate, thin host project composes every module's `Api` project into one running ASP.NET Core app. This supersedes the flat single-`Api`/single-`Application`/single-`Domain`/single-`Infrastructure` layout used in both the legacy app and the earlier `rewrite-in-rust-kekw` attempt (see [MIGRATION.md §3](./MIGRATION.md#3-second-rewrite-attempt-rewrite-in-rust-kekw-branch)) — the Clean/Onion layering is kept, but it's now applied *per module*, all the way out to the HTTP surface, instead of once for the whole app.

```
src/
  Accounts/                    # module folder named after the plural — see naming note below
    Accounts.Domain/            # entities, value objects, domain events — zero dependencies
    Accounts.Application/       # Mediator commands/queries/handlers — depends only on Accounts.Domain
    Accounts.Infrastructure/    # Marten wiring, Keycloak token-exchange calls — depends on Accounts.Application + Accounts.Domain
    Accounts.Api/                # endpoints AND DTO mappers for this module — depends on Accounts.Infrastructure (+ Application, Domain transitively)
      AccountEndpoints.cs        # public static class AccountModule { MapAccountModule(this WebApplication app), AddAccountModule(this IServiceCollection services) }
      AccountDto.cs               # static AccountDto.Create(Account source), record CreateSessionRequestDto.ToCommand()
  Banking/
    Banking.Domain/
    Banking.Application/
    Banking.Infrastructure/
    Banking.Api/                # same shape as Accounts.Api
  Api/                          # the thin ASP.NET Core host — composition root, no business logic
    Program.cs                  # references every module's *.Api project only; calls
                                 #   builder.Services.AddAccountModule().AddBankingModule()...
                                 #   app.MapAccountModule().MapBankingModule()...
  Bridge/                        # separate Worker Service — NOT a module, a sibling deployable (see §3.2)
```

**Naming convention:** `AssemblyName` and `RootNamespace` are set once, centrally, in the repo-root `Directory.Build.props` as `ELifeRPG.$(MSBuildProjectName)` — individual `.csproj` files don't declare either. This means a module's folder/project name *is* its namespace suffix, so it must be picked to avoid colliding with the module's own primary type: `Account` the module would produce namespace `ELifeRPG.Account.Domain` containing type `ELifeRPG.Account.Domain.Account` — a namespace segment with the exact same name as a type in it, which forces `global::`-qualification everywhere. Pluralizing the module folder (`Accounts`, matching the legacy app's own `Domain.Accounts.Account` convention) avoids this entirely and is the reason the module above is `src/Accounts/` with `Accounts.Domain`/`Accounts.Application`/etc. projects, even though the bounded context is conceptually "Account." Only pluralize when the module name and its primary aggregate type would otherwise match (`Banking` containing `Bank`/`BankAccount` doesn't need it).

**Dependency rules (the actual boundary enforcement mechanism):**

- `*.Domain` has no project references at all beyond a shared kernel of primitives (see below).
- `*.Application` references only its own module's `*.Domain`.
- `*.Infrastructure` references only its own module's `*.Application` + `*.Domain`.
- `*.Api` references only its own module's `*.Infrastructure` (and transitively `*.Application`/`*.Domain`) — it owns both the Minimal API endpoint definitions and the DTO↔domain mapping for that module, and exposes one pair of extension methods (`AddXModule`, `MapXModule`) as its entire public surface.
- `src/Api` (the host) references **only** each module's `*.Api` project — never a module's `Domain`/`Application`/`Infrastructure` directly. It contains no business logic, just `Program.cs` calling every module's `AddXModule`/`MapXModule`.
- **No module project may reference another module's `Domain`, `Infrastructure`, or `Api` project, under any circumstances.** The one narrow exception is `Application`-to-`Application`: if `Characters.Application` needs data owned by `Accounts`, it may reference `Accounts.Application` *only* to use its public Mediator request/response record types (e.g. `AccountLookupQuery`/`AccountLookupResult`) and dispatches through `IMediator` exactly as if `Accounts` were already a separate service. This is what keeps the modulith extractable into real services later without a rewrite, consistent with the "any new consumer follows the same pattern" philosophy in [§8](#8-future-considerations). **Handler classes must be `public`, not `internal`** — this was originally documented as `internal` (enforced by the compiler), but centralizing the Mediator dispatcher in the host (see below) means handlers are constructed from generated code compiled into a *different* assembly (`src/Api` or a test project), so `internal` produces `CS0122` at build time. The module-boundary rule (only request/response contracts are meant to be used by other modules, not handlers directly) is enforced by convention/review now, not by the compiler.
- **Cross-module atomic writes** are a second, narrower exception to the isolation rule above: an orchestrating command in one module's `Application` layer may call another module's explicitly-named `I<X>RepositoryFactory.CreateFor(handle)` (e.g. `ICompanyRepositoryFactory`) to obtain a repository bound to a shared `ICrossModuleTransaction`, when a single operation must commit events to both modules atomically — this is possible because every module's data lives in the same physical PostgreSQL database, just separate schemas. It never reaches into `Domain`/`Infrastructure`/`Api` directly, only a factory the target module chooses to expose from its own `Application` layer, and is reserved for named, individually-reviewed orchestrating commands (e.g. `Banking.Application.Companies.PurchaseCompanySharesCommand`, `Shops.Application.Shops.PurchaseListingCommand`), not a general write-anywhere mechanism. See `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`.
- **Deciding between the atomic-transaction coordinator above and a saga/process-manager:** the deciding factor is not which modules are involved, it's whether both modules' data lives in the same physical database — true for every module today (one Postgres instance, separate schemas). When it's true, an operation that must never leave a partial/stuck state (e.g. debiting one module's aggregate without a corresponding grant in another) uses `ICrossModuleTransaction`, full stop. A saga/compensating-action pattern is reserved for the case where atomicity is genuinely unavailable: a real external system, a physically separate database, or a step that must wait across a genuine async/durable boundary (e.g. something waiting on the Bridge across multiple requests). `Shops.Application.Shops.PurchaseListingCommand` was originally built as a saga before this mechanism existed and was migrated once it did (see `docs/superpowers/specs/2026-08-16-purchase-listing-cross-module-migration.md`) — treat that as the precedent: default new cross-module writes to the atomic coordinator, not a saga, whenever both modules share this database.
- **Cross-module ID references** (e.g. a `BankAccount` in `Banking` needs to reference the `AccountId`/`CharacterId` that `Account`/`Characters` owns) go through a small shared-kernel project (e.g. `Shared.Kernel`) containing **only** `StronglyTypedId`-based ID value types — never full entities, aggregates, or shared base classes. Each module's aggregates and events are defined independently within that module; there is no shared `AggregateBase`/`DomainEvent` base type in this codebase today. Validating that an ID is real (not just well-formed) happens via a Mediator query into the owning module, not a cross-module join.

**Marten per module:** each module's `Infrastructure` project owns its own Marten `IDocumentStore`, scoped to its own PostgreSQL schema (`account`, `characters`, `banking`, `companies`, …) within a single Postgres instance/compose service. This keeps deployment simple (one Postgres container) while making cross-module schema access structurally awkward — the storage layer reinforces the same boundary the project-reference graph enforces at compile time. The first module (`Accounts`) uses Marten's *default* store (`AddMarten`, giving handlers an auto-injected scoped `IDocumentSession`); every module after it (starting with `Characters`) instead registers a *secondary, named* store (`AddMartenStore<ICharactersStore>`) because only the default store gets DI-injected sessions — the repository owns and disposes its own `IDocumentSession` explicitly instead (see gotcha 6 below). A module isn't limited to one aggregate type per store: `Banking` registers both `Bank` and `BankAccount` as separate `SingleStreamProjection`s against the same `IBankingStore`/`banking` schema, each via its own repository (`MartenBankRepository`/`MartenBankAccountRepository`) opening its own session from that shared store — confirmed to coexist without registration conflicts.

**Multi-aggregate atomic operations (verified via `Banking`'s money transfer, 2026-08-14):** when one Application-layer operation needs to update two aggregates of the *same* module atomically (e.g. debiting one `BankAccount` and crediting another), have the repository own one `IDocumentSession` for the whole request (already true per the pattern above) and append both aggregates' events to that same session before a single `SaveChangesAsync()` — Marten commits both streams in one Postgres transaction. No special API needed beyond calling `session.Events.Append(id, event)` once per aggregate before the shared save; confirmed with a real `TransferCommand` moving money between two live `BankAccount` streams, verified both via an integration test and a manual end-to-end check (no partial-update window observed). This only works because both aggregates are internal to the *same* module and the *same* repository instance — a transfer spanning two modules instead uses the cross-module atomic-write mechanism described above (`ICrossModuleTransaction`), not this shortcut. Confirmed via `Banking`'s `PurchaseCompanySharesCommand`, which atomically debits a `BankAccount` and credits `Companies`' `Company.Shares` in one shared Postgres transaction — see `docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md`. `Shops.Application.Shops.PurchaseListingCommand` was later migrated onto this same mechanism, replacing a saga it had originally been built with only because `ICrossModuleTransaction` didn't exist yet at the time — not because the saga had failed on the merits — see `docs/superpowers/specs/2026-08-16-purchase-listing-cross-module-migration.md`.

**Catch domain guard exceptions in the Application handler, not the endpoint.** When a domain method enforces an invariant by throwing (e.g. `BankAccount.Withdraw` throwing `BankAccountAuthorizationException`/`InsufficientBalanceException` for an unauthorized caller / insufficient balance — same "invariant lives in the domain" convention as `Account.Lock()`'s `AccountStatusException`), the handler should `catch` those specific exception types and map them to dedicated union result cases (`WithdrawResult.NotAuthorized`/`InsufficientBalance`), not let them propagate to the endpoint as unhandled exceptions. This keeps the `Api` layer's `switch` over the union exhaustive-checked and free of `try`/`catch`, while the domain still enforces its invariant unconditionally regardless of caller (a direct unit test calling `Withdraw` still gets a real exception). Reserve this for exceptions representing a *business* rule violation the caller can reasonably trigger — not programming errors (`ArgumentOutOfRangeException` for a nonsensical negative amount, for instance, is left to propagate as a `500`; see `MIGRATION.md §7` for why that's an accepted gap, not an oversight).

**When an invariant needs a cross-module check, resolve it in the Application handler and pass a plain `bool` into the domain method — never let Domain make the query itself.** `BankAccount.Withdraw`/`TransferOut` originally decided authorization internally (comparing the acting `CharacterId` to a single stored owner). Once accounts could be owned by either a `Character` or a `Company` (see `MIGRATION.md §9`), Corporate authorization needed a `Companies`-module permission lookup — a `Mediator` dispatch, which `*.Domain` projects are never allowed to make (they have zero package/project references beyond the shared kernel, by design). The fix: the domain method's signature becomes `Withdraw(CharacterId actingCharacterId, bool isAuthorized, decimal amount)` — the *acting* character is still always recorded on the resulting event (for audit purposes, regardless of ownership type), but *how authorization is decided* is entirely the caller's problem. A shared Application-layer helper (`Banking.Application.Common.BankAccountAuthorization.IsAuthorizedAsync`) resolves the bool per ownership type — a plain field comparison for Personal, a `Mediator` query into the owning module for Corporate — before either `WithdrawHandler` or `TransferHandler` calls into the aggregate. Domain stays cross-module-ignorant and fully unit-testable (pass `isAuthorized: true`/`false` directly, no mocking a query dispatcher); only the Application layer needs the real infrastructure.

**Centralized Mediator dispatcher — only the host references `Mediator.SourceGenerator`.** `Mediator` generates its `IMediator` implementation and dispatch tables per-*assembly*, scanning whatever `options.Assemblies` lists. If two modules each call their own `AddMediator()` (each with the source generator referenced), only the first-registered `IMediator` "wins," and requests routed to the other module's handlers throw `MissingMessageHandlerException` at runtime — this is silent at compile time. The fix, verified via an isolated smoke test before applying it for real: every module's `*.Application` project references only `Mediator.Abstractions`; only `src/Api` (the host) references `Mediator.SourceGenerator` and makes **one** `AddMediator(options => options.Assemblies = [ /* every module's AssemblyMarker */ ])` call, with `options.ServiceLifetime = ServiceLifetime.Transient` (Mediator defaults handlers to `Singleton`, which `WebApplicationBuilder.Build()` correctly rejects as a captive dependency once a handler depends on a scoped `IDocumentSession`). Each `*.Application` project exposes an empty `public static class AssemblyMarker;` purely so the host has a type to point `options.Assemblies` at. Any project that is its own composition root outside the host — e.g. an xUnit integration test project — needs `Mediator.SourceGenerator` itself, for the same reason the host does. **A given composition root gets exactly one `AddMediator(...)` call site, full stop — even across multiple classes in the same project.** The generator scans the whole compilation for `AddMediator` invocations and builds one static dispatch table; a *second* test class in an integration test project calling `services.AddMediator(options => options.Assemblies = [...])` with the identical assembly list still fails to build (`MSG0007: Assemblies can only be configured once`) — the generator doesn't care that the two call sites agree, only that there are two. When a test project's coverage grows past one test class, extract the setup into a single shared static factory (e.g. `TestServices.BuildProvider()`) that every test class calls, rather than each class doing its own `AddMediator`.

**Verified Marten integration pattern (built and runtime-tested end-to-end against live Postgres, 2026-08-14, via the `Accounts`, `Characters`, `Banking`, and `Companies` modules — all four modules in the original architecture plan):** nine gotchas that apply to every future module, not just these:
1. **`Apply`/`Create` convention methods must live where Marten's source generator can see them — which is `*.Infrastructure`, not `*.Domain`.** Marten's aggregation is driven by a compile-time source generator, not reflection, and it only runs in the project that has the `Marten` package reference. Since `*.Domain` correctly has zero package references, putting `Apply`/`Create` directly on the domain aggregate fails at runtime with `InvalidProjectionException`. The fix: declare a `partial` projection class in `*.Infrastructure` (e.g. `AccountProjection : SingleStreamProjection<Account, AccountId>`) whose `Create`/`Apply` methods simply delegate to the aggregate's own methods — the aggregate keeps its behavior (`Lock()`, `Unlock()`, invariants) and stays Marten-free; only the event-replay wiring lives in Infrastructure.
2. **Aggregate properties need `[JsonInclude]` if their setters are non-public.** Marten's inline-snapshot documents are plain JSON round-tripped via reflection-based (de)serialization on read (`session.Query<T>()` deserializes the stored blob directly — it does not replay events through `Apply`). A `private set` property serializes fine (needs only the getter) but silently deserializes to `default` on read, since `System.Text.Json` won't touch a non-public setter without `[JsonInclude]`. Symptom looks like "query returns an object but every field is zeroed," not an exception — easy to misdiagnose as a query bug.
3. **`StronglyTypedId` values must be unwrapped to their primitive (`.Value`) when starting/appending to an event *stream*.** `session.Events.StartStream<T>(id, events)`/`session.Events.Append(id, events)` expect a raw `Guid`; passing an `AccountId` struct directly doesn't fail to compile (there's an `object[] events` params overload it silently matches instead), it fails at runtime by treating the id as an extra event. Likewise, LINQ queries comparing a strongly-typed property need `.Value` on both sides (`x.BohemiaId.Value == bohemiaId.Value`), not a direct struct comparison.
4. **...but the opposite is true for `session.LoadAsync<TDocument>(id)` — pass the `StronglyTypedId` itself, not `.Value`.** Marten inspects the document type's own `Id` property to determine the expected id type; since `Account.Id` is declared as `AccountId` (not `Guid`) and `StronglyTypedId`'s conversion operators make it recognizable, Marten's document storage — unlike the *event stream* API above — expects the strongly-typed id directly. Calling `session.LoadAsync<Account>(accountId.Value, ...)` compiles fine but throws `Marten.Exceptions.DocumentIdTypeMismatchException: ... the id type ... is ELifeRPG.Shared.Kernel.AccountId, but Guid was used` at runtime. This was only caught when `Characters.Application`'s cross-module `AccountLookupQuery` handler first exercised `IAccountRepository.FindByIdAsync` under a real Postgres connection — it isn't visible from a compile-only check.
5. **Namespace the module's root as plural** (`ELifeRPG.Accounts.*`, not `ELifeRPG.Account.*`) when the aggregate type itself is named after the module (`Account`) — otherwise every reference to the type needs `global::`-qualification to disambiguate it from the identically-named namespace segment. Matches the legacy app's own convention (`Domain.Accounts.Account`), just apply it to the new per-module root namespaces too.
6. **A repository backed by a secondary Marten store that owns its own `IDocumentSession` (rather than an injected scoped one) typically only implements `IAsyncDisposable`, not `IDisposable`.** .NET's built-in `ServiceProviderEngineScope.Dispose()` (the synchronous path, e.g. `using var scope = provider.CreateScope()`) throws `InvalidOperationException: '...' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.` the moment such a repository is resolved into that scope. ASP.NET Core's real request pipeline always disposes scopes asynchronously, so this never surfaces through `src/Api` — it only bites hand-rolled composition roots (test projects, smoke tests) that use the sync `CreateScope()`/`using` pattern instead of `CreateAsyncScope()`/`await using`.
7. **LINQ filters on a nullable `StronglyTypedId` property (`CharacterId?`, not `CharacterId`) translate correctly, but need the null-check spelled out explicitly** — `x.OwnerCharacterId != null && x.OwnerCharacterId!.Value.Value == characterId.Value`, not just `x.OwnerCharacterId!.Value.Value == characterId.Value`. Every prior module's strongly-typed-id filters were on non-nullable properties (`x.BohemiaId.Value == ...`); `Banking`'s dual-ownership `BankAccount` (see `MIGRATION.md §9`) was the first to need a nullable one, since exactly one of `OwnerCharacterId`/`OwnerCompanyId` is set depending on `Type`. Verified against live Postgres with a test asserting the filter actually *excludes* non-matching rows (not just happens to include the one row present) — worth being that deliberate any time a "did this filter really filter" bug is possible.
8. **`IEvent` (the type `session.Events.FetchStreamAsync`/raw stream-reading APIs return) lives in `JasperFx.Events`, not `Marten.Events`.** Marten 9.x moved a chunk of its core event-sourcing types — including `ProjectionLifecycle` (used everywhere in this codebase's `*.Infrastructure` `ServiceCollectionExtensions`, as `JasperFx.Events.Projections.ProjectionLifecycle.Inline`) and now `IEvent` — into the separate `JasperFx.Events` package. `using Marten.Events;` compiles fine (the namespace itself exists and has other members) but doesn't contain `IEvent`, producing `CS0246: The type or namespace name 'IEvent' could not be found`. Hit while building `Banking`'s transaction-history endpoint (`MIGRATION.md §10`), which reads an account's own event stream back out for display — `using JasperFx.Events;` fixes it. Whenever a Marten type "should" be in `Marten.*` but isn't found, check `JasperFx.Events`/`JasperFx.Events.Projections` before assuming something else is wrong.
9. **A `SessionOptions.ForTransaction`-bound session cannot use Marten's version-checked event-append machinery — neither `FetchForWriting` nor an explicit-version `Append` — even though both work fine on an ordinary scoped/lightweight session.** Version tracking for a stream is broken at the Marten/JasperFx level on a session opened via `SessionOptions.ForTransaction(existingTransaction, shouldAutoCommit: false)` (the shape every cross-module writer in this codebase uses to join a shared `ICrossModuleTransaction`'s `NpgsqlTransaction`), so relying on it to serialize two concurrent writers racing the same aggregate silently stops working the moment a handler is migrated onto `ICrossModuleTransaction` — no exception, no compile-time signal, just a lost concurrency guarantee. Verified against live Postgres via 3 spikes while migrating `Shops.Application.Shops.PurchaseListingCommand` off its saga onto `ICrossModuleTransaction` (2 NO-GO — `FetchForWriting` and explicit-version `Append` each independently confirmed broken on a `ForTransaction`-bound session — then 1 GO for the workaround below; full history in `docs/superpowers/specs/2026-08-16-purchase-listing-cross-module-migration.md`). **The verified workaround:** take a Postgres-native row lock as raw SQL against the shared `NpgsqlTransaction` — `SELECT id FROM <schema>.mt_doc_<aggregate> WHERE id = @id FOR UPDATE` (filter the doc table's primary key, or the query seq-scans the table) — *before* loading/mutating the aggregate, held until the shared transaction commits or rolls back, followed by a plain, unversioned `session.Events.Append(id, domainEvent)`. The row lock itself (not Marten) is what gives "two concurrent writers can never both succeed." See `MartenShopListingRepository.ReserveStockAsync` for the reference implementation and `tests/Shops.IntegrationTests/CrossModuleRowLockSpikeTests.cs` for the spike that proved it out. **Updated 2026-08-22:** the predicate was originally `WHERE tenant_id = @tenant AND id = @id`, filtering both columns of the doc table's composite `(tenant_id, id)` primary key — conjoined multi-tenancy made `tenant_id` part of the key, so both columns had to be filtered together or the query would seq-scan the table. Removing conjoined tenancy from Banking, Companies, and Shops (the last three modules that still had it) dropped `tenant_id` from the key entirely, leaving `id` alone as the primary key, confirmed live for all three: `CREATE UNIQUE INDEX pkey_mt_doc_bankaccount_id ON banking.mt_doc_bankaccount USING btree (id)` (and the equivalent `pkey_mt_doc_company_id`/`pkey_mt_doc_shoplisting_id` on the other two). `WHERE id = @id` is therefore an exact leading-column match on a unique index — satisfied by construction, a stronger guarantee than any single `EXPLAIN` run. Caveat for whoever next runs `EXPLAIN` against a small/freshly-seeded table and sees `Seq Scan`: that's expected, not a regression — on the small tables seeded during this migration (35-98 rows) Postgres's cost-based planner correctly preferred a seq scan regardless of the index, and the index path itself was only confirmed by forcing it with `SET enable_seqscan = off`. The underlying point survives unchanged: filter the doc table's full primary key, whatever it currently is, or risk a seq-scan on a table large enough for the planner to care.

**DTO mapping — no AutoMapper:** every DTO lives in its module's `*.Api` project, next to the endpoint that uses it, and owns its own mapping instead of a separate `*Profile` class:

```csharp
public sealed record AccountDto
{
    public required Guid AccountId { get; init; }
    public required bool Locked { get; init; }

    public static AccountDto Create(Account source) => new()
    {
        AccountId = source.Id.Value,
        Locked = source.Status == AccountStatus.Locked,
    };
}

public sealed record CreateSessionRequestDto
{
    public required Guid BohemiaId { get; init; }

    public CreateSessionCommand ToCommand() => new(new GameId(BohemiaId));
}
```

Note the asymmetry: outbound DTOs map *from* an aggregate/projection (`Create(source)`), but inbound request DTOs map *to a command*, not directly into an aggregate — event-sourced aggregates are only ever mutated through their own factory/behavior methods (`Account.Create(...)`, `account.Lock()`), so a request DTO's `ToCommand()`/`ToModel()` method hands off to the Application layer, which is what actually calls those methods. A DTO never constructs or rehydrates an aggregate itself.

**Native `union` types instead of `OneOf`:** the project targets **.NET 11 preview**, and every Mediator command/query result with more than one meaningfully different outcome uses a real C# `union` declaration — `OneOf` is dropped outright, not kept as a fallback. Both legacy branches (`main` and `rewrite-in-rust-kekw`) used `OneOfBase<T>`; that dependency is not carried forward.
**Verified against SDK `11.0.100-preview.6.26359.118` (2026-08-14) — syntax, construction, pattern matching, and exhaustiveness all confirmed working:**
```csharp
public union CreateSessionResult(CreateSessionResult.Created, CreateSessionResult.Locked)
{
    public record Created(AccountId AccountId, KeycloakUserId KeycloakUserId);
    public record Locked(AccountId AccountId);
}
```
This is a genuine union type, not an inheritance hierarchy: `Created`/`Locked` are ordinary unrelated types (not subclasses of `CreateSessionResult`) that implicitly convert to/from it — `return new CreateSessionResult.Locked(account.Id);` works directly. Consuming code pattern-matches with `switch`, and the compiler checks exhaustiveness for real: removing a case from a `switch` produces `CS8509` naming the specific missing case (e.g. `the pattern 'CreateSessionResult.Locked' is not covered`), not a generic sealed-hierarchy inference.
**Corrected twice while building this — worth recording so it isn't repeated:** the first pass tried `union Result { Ok(int Value), Error(string Message) }` and concluded (wrongly) that no union feature existed at all, because that declaration shape doesn't match the real grammar. The second pass found `closed record` hierarchies instead, which *do* work and *are* a real preview feature, but are a different, narrower mechanism (a restricted inheritance hierarchy) — not what "union types" refers to. The real feature was found by grepping the Roslyn compiler binary itself for `union`-related diagnostic strings (`ERR_MissingUnionCaseTypes`, `UnionDeclarationSyntax`, etc.), which revealed the actual grammar: `union Name(CaseType1, CaseType2, ...)`, case types listed in parens like a primary constructor, not a `:` base list (that parses as ordinary interface implementation and fails with `CS9370`).
**Convention:** any Mediator `IRequest<TResponse>` whose handler can produce more than one materially different shape of result (not just a single success DTO) should model `TResponse` as a `union`, with its case types nested inside the union body for name scoping (`CreateSessionResult.Created`, not a bare `Created`). `CreateSessionResult` is the reference example.
**Implementation note:** pin the exact preview version in `global.json` (`rollForward` disabled rather than `latestMinor`, unlike the legacy app's `global.json`) so a routine `dotnet` upgrade on a dev machine can't silently change compiler behavior underneath the build. This is preview grammar and could still shift before GA — re-verify on each preview bump rather than assuming stability.

---

## 10. Rollout Recommendation

1. Central API + Bridge repo scaffolded (single `.sln`, modulith layout per §9e) with the first module (`Account`), Keycloak + PostgreSQL via Docker Compose, Client Credentials auth working end-to-end.
2. OpenTelemetry instrumentation + Grafana/Loki/Tempo/Prometheus stack wired in early, so every subsequent step is observable from the start.
3. Marten/PostgreSQL event store + batch ingestion endpoint; OpenAPI spec published; Kiota-generated C# client adopted by the Bridge.
4. Admin UI repo stood up (Vue + Kiota-generated TS client), Authorization Code + PKCE login against Keycloak.
5. SignalR-based live delivery to Admin UI (and NPC Simulation Module once its stack is chosen), with REST catch-up for reconnects.
6. NPC Simulation Module repo stood up once its runtime is decided; generates its own client from the same OpenAPI spec.
7. Partitioning/retention once event volume is measured.
8. Kubernetes migration and/or message broker only if and when load actually requires either.
