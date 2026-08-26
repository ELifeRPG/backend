# Accounts

Bootstraps (or looks up) an `Account` for a player's Bohemia ID, provisioning a Keycloak user if needed. See [MIGRATION.md §5](../MIGRATION.md#5-migration-plan-feature-1--accounts--sessions) for how this replaced legacy's unauthenticated `POST /v1/sessions`.

Assumes `src/Api` is running (see the main [README](../README.md#run)) and you're calling from inside the devcontainer, which is on the Compose network — `keycloak` resolves by hostname there. From your host machine, use `http://localhost:8180` for Keycloak instead.

`POST /api/accounts/session-bootstrap` requires a bearer token with the `gameserver:session:create` scope — get one from the pre-provisioned dev client:

```sh
BRIDGE_TOKEN=$(curl -s -X POST http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=gameserver-dev" -d "client_secret=local-dev-only-not-a-real-secret" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -X POST http://localhost:5100/api/accounts/session-bootstrap \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"bohemiaId":"11111111-1111-1111-1111-111111111111"}'
```

`$BRIDGE_TOKEN` is reused across the other feature docs ([Characters](./characters.md), [Banking](./banking.md), [Companies](./companies.md)) — mint it once per shell session.

`session-bootstrap` always returns `200` — a blocked account comes back as `{"status": "blocked", ...}` with no error, rather than a `403`.

## Locking and unlocking an account

`POST /api/accounts/{accountId}/lock` and `POST /api/accounts/{accountId}/unlock` require a bearer token with the `accounts:manage` scope — deliberately not granted to `gameserver-dev`, since banning a player is an admin/staff action, not something the game server does. Get one from the pre-provisioned dev client:

```sh
STAFF_TOKEN=$(curl -s -X POST http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -i -X POST http://localhost:5100/api/accounts/$ACCOUNT_ID/lock -H "Authorization: Bearer $STAFF_TOKEN"
curl -i -X POST http://localhost:5100/api/accounts/$ACCOUNT_ID/unlock -H "Authorization: Bearer $STAFF_TOKEN"
```

Both are idempotent (locking an already-locked account, or unlocking an already-active one, still returns `204`) and return `404` for an unknown `accountId`. Locking also disables the account's Keycloak user (correct account hygiene — it blocks normal login) but the thing that actually stops a locked account from getting a new player token is the status gate inside `BridgeTokenProvider.ExchangeForPlayerTokenAsync` itself: it never attempts the Keycloak token-exchange call for a blocked account in the first place. See [ARCHITECTURE.md §4.3](../ARCHITECTURE.md#43-player-identity-token-exchange) for why Keycloak's `enabled` flag doesn't independently block that exchange grant. An already-issued player token is unaffected until it naturally expires (the realm's access token lifespan is 5 minutes).

## Listing accounts

`GET /api/accounts?search=<term>` requires the `accounts:manage` scope (same bar as lock/unlock). `search` optionally filters by a Bohemia ID substring match; omit it (or pass an empty string) to list every account.

```sh
curl -s "http://localhost:5100/api/accounts?search=" -H "Authorization: Bearer $STAFF_TOKEN" | python3 -m json.tool
```

Returns `{"accounts": [{"id": ..., "bohemiaId": ..., "discordUsername": null, "status": "Active"}, ...]}`. `discordUsername` is always `null` for now — that field isn't modeled on the `Account` aggregate yet.

## Managing an account's roles

`GET/PUT/DELETE /api/accounts/{accountId}/roles[/{roleName}]` require the caller's token to carry the `admin` **realm role** in its `realm_access.roles` claim — a stricter, genuinely role-conditional bar than `accounts:manage`, since granting/revoking realm roles (including roles that grant `accounts:manage` itself) is more sensitive than ordinary staff access. This uses the same `RealmRoleAuthorization` mechanism as `WhitelistReviewerPolicy`/`ServerAdminPolicy` (see `Accounts.Api/Common/RealmRoleAuthorization.cs`), not a client scope — Keycloak has no scope-level role-conditional mechanism, so the earlier `accounts:roles:manage` client scope was retired in favor of this. Whether `admin` actually surfaces in a given token depends on two things Keycloak checks independently: the user (or service account) must actually hold the `admin` realm role, *and* the client the token was issued to must have an `admin` entry in the realm's `scopeMappings` (see `infra/keycloak/eliferpg-realm.json`) — both `staff-admin-dev` and `eliferpg-portal` have that mapping today, so a human staff member can be granted `admin` and use the "Roles" screen in `eliferpg-webapp` directly, no longer service-account-only. `$STAFF_TOKEN` from the section above works here too, since `staff-admin-dev`'s service account already holds `admin`.

```sh
curl -s http://localhost:5100/api/accounts/$ACCOUNT_ID/roles -H "Authorization: Bearer $STAFF_TOKEN" | python3 -m json.tool
curl -i -X PUT http://localhost:5100/api/accounts/$ACCOUNT_ID/roles/whitelist-reviewer -H "Authorization: Bearer $STAFF_TOKEN"
curl -i -X DELETE http://localhost:5100/api/accounts/$ACCOUNT_ID/roles/whitelist-reviewer -H "Authorization: Bearer $STAFF_TOKEN"
```

All three are idempotent and return `404` for an unknown `accountId` or a `roleName` that isn't an actual Keycloak realm role. The available-roles list always excludes Keycloak's own built-ins (`default-roles-eliferpg`, `offline_access`, `uma_authorization`).

## Game server registry

One deployment is one **hive**: a set of game servers (one server = one map) sharing all gameplay data. `POST`/`GET /api/game-servers` require the caller's token to carry the `server-admin` **realm role** (`RealmRoleAuthorization`, same mechanism as the `admin`-role endpoints above) — `staff-admin-dev`'s service account already holds it, so `$STAFF_TOKEN` from the [locking section](#locking-and-unlocking-an-account) works here too.

```sh
curl -s -X POST http://localhost:5100/api/game-servers \
  -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" \
  -d '{"clientId":"gameserver-dev","displayName":"Server 1","mapName":"Everon"}' | python3 -m json.tool

curl -s http://localhost:5100/api/game-servers -H "Authorization: Bearer $STAFF_TOKEN" | python3 -m json.tool
```

`POST` returns `200` with `{"id": ..., "clientId": "gameserver-dev", "displayName": "Server 1", "mapName": "Everon"}` — `id` is a generated `GameServerId`, distinct from `clientId` (the Keycloak client mapping) so renaming or rotating the Keycloak client later doesn't orphan anything referencing this server. Re-registering an already-known `clientId` updates its `displayName`/`mapName` in place rather than erroring. `GET` lists every server registered in the hive. An unregistered `client_id` is no longer treated as an implicit default server anywhere in the system — a gameserver must be registered here before it can call any module endpoint that resolves `ICurrentGameServer` (character/shop creation, for example). Session-bootstrap itself doesn't touch this registry — `CreateSessionHandler` has no `IGameServerRepository` dependency — so it works regardless of registration.

## Hive settings

Deployment-wide settings that apply to every server in the hive live in a single settings document, gated the same way as the game server registry (`server-admin` realm role):

```sh
curl -s http://localhost:5100/api/hive/settings -H "Authorization: Bearer $STAFF_TOKEN" | python3 -m json.tool

curl -s -X PATCH http://localhost:5100/api/hive/settings \
  -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" \
  -d '{"whitelistEnabled":true}' | python3 -m json.tool
```

Both return `{"whitelistEnabled": true|false}`. `PATCH` is a partial update (`whitelistEnabled` is nullable in the request; omitting it leaves the setting unchanged) — the same "omitted fields unchanged" convention `PATCH /api/game-servers/{clientId}` already used before `WhitelistEnabled` moved off `GameServer` onto this hive-level document.

## Whitelisting

Whitelist applications are hive-wide, not per-server: an account applies once and, once approved, can play on any server in the hive — there is no `serverClientId` on the application anymore. `WhitelistEnabled` (above) is the single hive-level gate that decides whether `session-bootstrap` requires an approved application at all.

Submitting an application uses the `gameserver:whitelist:write` scope — `$BRIDGE_TOKEN` from the [top of this doc](#accounts) already carries it:

```sh
curl -s -X POST http://localhost:5100/api/whitelist-applications \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"accountId":"'"$ACCOUNT_ID"'","applicationText":"Please let me play!"}' | python3 -m json.tool
```

Returns `200` with `{"whitelistApplicationId": ..., "status": "Open"}`; `404` for an unknown `accountId`, `409` if that account already has a pending application.

Reviewing applications requires the `whitelist-reviewer` realm role — `$STAFF_TOKEN` holds it too:

```sh
curl -s "http://localhost:5100/api/whitelist-applications?status=Open" -H "Authorization: Bearer $STAFF_TOKEN" | python3 -m json.tool

curl -i -X POST http://localhost:5100/api/whitelist-applications/$APPLICATION_ID/start-review -H "Authorization: Bearer $STAFF_TOKEN"
curl -i -X POST http://localhost:5100/api/whitelist-applications/$APPLICATION_ID/approve -H "Authorization: Bearer $STAFF_TOKEN"
```

`start-review`/`approve`/`reject` are each idempotent within their target state, return `404` for an unknown application id, and `409` if the application isn't in the state the transition requires (`InReview` for `approve`/`reject`). The list endpoint filters by exactly one `status` (`Open`/`InReview`/`Approved`/`Rejected`) and is already cross-server — it never needed a per-server view even before this change.
