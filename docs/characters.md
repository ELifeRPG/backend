# Characters

Creating and listing characters, plus per-character session tracking (`SessionActive`/`SessionStartedAt`/`SessionEndedAt`) — see [MIGRATION.md §6](../MIGRATION.md#6-migration-plan-feature-2--characters) for the module and how session tracking was added on top of it.

Character data is isolated per gameserver — a character created via one gameserver's Bridge token is invisible to every other gameserver in the same deployment, even though accounts (see [Accounts](./accounts.md)) are shared across all of them.

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and an `accountId` from a session-bootstrap call.

`POST /api/characters` (and `GET /api/accounts/{accountId}/characters`) require the `gameserver:characters:write` scope (also granted to `gameserver-dev`):

```sh
curl -X POST http://localhost:5100/api/characters \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"accountId":"<accountId from the session-bootstrap response>","name":"Alice"}'
```

## Sessions

A character's session is distinct from the account-level connection the Bridge tracks (see [Bridge](./bridge.md)) — it starts when a player actually *selects* that character in-game, not just when they connect.

```sh
curl -X POST http://localhost:5100/api/characters/$CHARACTER_ID/sessions \
  -H "Authorization: Bearer $BRIDGE_TOKEN"

curl -X DELETE http://localhost:5100/api/characters/$CHARACTER_ID/sessions \
  -H "Authorization: Bearer $BRIDGE_TOKEN"
```

Both are idempotent by design, not guarded: starting a session that's already active just supersedes it (new `SessionStartedAt`, `SessionEndedAt` cleared) rather than throwing. This is deliberate — there's no cleanup yet for a character left "active" by an ungraceful gameserver crash/restart, so a stale flag must not permanently block that character from reselecting. See `Character.StartSession()`'s doc comment (`src/Characters/Characters.Domain/Character.cs`) for the full reasoning, including the not-yet-built alternative (reconciling stale sessions by gameserver instance identity on Bridge startup).

`GET /api/accounts/{accountId}/characters` includes the session fields in its response, so you can observe the effect:

```sh
curl http://localhost:5100/api/accounts/$ACCOUNT_ID/characters -H "Authorization: Bearer $BRIDGE_TOKEN"
```
