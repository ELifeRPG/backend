# Accounts

How an `Account` comes into existence, and how a player's in-game (Bohemia) identity gets attached to it. See [MIGRATION.md §5](../MIGRATION.md#5-migration-plan-feature-1--accounts--sessions) for how this replaced legacy's unauthenticated `POST /v1/sessions`.

Assumes `src/Api` is running (see the main [README](../README.md#run)) and you're calling from inside the devcontainer, which is on the Compose network — `keycloak` resolves by hostname there. From your host machine, use `http://localhost:8180` for Keycloak instead.

Mint the gameserver token first; `$BRIDGE_TOKEN` is reused across the other feature docs ([Characters](./characters.md), [Banking](./banking.md), [Companies](./companies.md), [World](./world.md), [Bridge](./bridge.md)) — mint it once per shell session.

```sh
BRIDGE_TOKEN=$(curl -s -X POST http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=gameserver-dev" -d "client_secret=local-dev-only-not-a-real-secret" -d "grant_type=client_credentials" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
```

## Identity is portal-first

**An account is created by web signup, never by joining a gameserver.** The Keycloak user comes from ordinary portal signup (Discord broker or local registration); the account is created the first time that user reaches the backend, well before they ever connect to a server, and therefore with **no Bohemia ID**. The player attaches their game identity afterwards, by typing an in-game PIN into Keycloak's own form.

This is worth stating plainly because it inverts what the endpoint names suggest: `session-bootstrap` **never creates anything**. It is a lookup. The creating endpoint is `POST /api/accounts/me`.

### Getting an `accountId`

`POST /api/accounts/me` creates (or returns) the account behind the calling **Keycloak user** — the `sub` on the token, not a body parameter. It requires the `account:self:manage` scope, which in the real flow the portal (`eliferpg-portal`, Authorization Code + PKCE) carries. That client has no service account and no direct-access grant, so there is no pure-`curl` path through it.

For local development, `account:self:manage` is therefore also assigned to `staff-admin-dev` as an **optional** client scope. Optional means it is never in a token unless explicitly requested, so this changes nothing about `$STAFF_TOKEN` as used everywhere else in these docs:

```sh
SELF_TOKEN=$(curl -s -X POST http://keycloak:8080/realms/eliferpg/protocol/openid-connect/token \
  -d "client_id=staff-admin-dev" -d "client_secret=staff-secret-change-me" -d "grant_type=client_credentials" \
  -d "scope=account:self:manage" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

ACCOUNT_ID=$(curl -s -X POST http://localhost:5100/api/accounts/me \
  -H "Authorization: Bearer $SELF_TOKEN" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['accountId'])")
echo $ACCOUNT_ID
```

Returns `{"accountId": "…", "created": true}`, and `created: false` on every call after the first — it is idempotent, keyed on the Keycloak user, so a portal page calling it on every load does not create a second account. The account it makes belongs to `staff-admin-dev`'s own service-account user and has no Bohemia ID, which is exactly the state a freshly-signed-up player is in and is all the other feature docs need.

*(If your Keycloak predates this and rejects the scope, assign it once with the admin API: `PUT /admin/realms/eliferpg/clients/{clientUuid}/optional-client-scopes/{scopeId}`, using an `admin-cli` token from the `master` realm. Recreating the stack re-imports `infra/keycloak/eliferpg-realm.json`, which now carries it.)*

`$ACCOUNT_ID` is what [Characters](./characters.md) needs.

### Joining: `session-bootstrap`

`POST /api/accounts/session-bootstrap` requires the `gameserver:session:create` scope (on `$BRIDGE_TOKEN`) and answers one question: *which account, if any, owns this Bohemia ID?*

```sh
curl -s -X POST http://localhost:5100/api/accounts/session-bootstrap \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"bohemiaId":"11111111-1111-1111-1111-111111111111"}' | python3 -m json.tool
```

**It always returns `200`.** Every outcome is reported in `status`, never as an error code:

| `status` | `accountId` | Means |
|---|---|---|
| `active` | set | Linked, not locked, whitelisted (or whitelisting is off). Let them play. |
| `blocked` | set | The account is locked. Not a `403`. |
| `not_whitelisted` | set | Whitelisting is on and this account has no approved application. |
| `unlinked` | **`null`** | **No account owns this Bohemia ID yet.** Nothing was created. |

The `unlinked` response carries a `linkPin` for the mod to display:

```jsonc
{ "accountId": null, "keycloakUserId": null, "status": "unlinked", "linkPin": "UVQQM8ZU" }
```

The player types that PIN into Keycloak's own linking form (the `link-bohemia-gameaccount` required action, from the `keycloak-bohemia-gameaccount` provider — which is why `infra/keycloak` builds a custom image rather than pulling stock Keycloak). Keycloak writes the binding onto their user; the *next* `session-bootstrap` for that Bohemia ID notices it, records it on the account, and returns `active` with a real `accountId`.

Two consequences worth knowing before you go looking for a bug:

- **A Bohemia ID nobody has linked provisions nothing.** No account, no Keycloak user. `accountId` is `null` and stays `null` until the player completes the PIN step in a browser.
- **`linkPin` can be `null` even on `unlinked`.** Keycloak refuses to mint a PIN for an already-bound Bohemia ID, which means the binding landed between the lookup and the mint — re-issue the call rather than showing a PIN that could never be redeemed.

There is no `curl`-only shortcut for the PIN step: it is a browser form on Keycloak by design, because it is the moment a human proves the game identity is theirs.

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
