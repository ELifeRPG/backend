# Skills

Per-character skill progression — numeric level + XP across a fixed catalog of Gathering and Crafting
skills (`Mining`, `Woodcutting`, `Fishing`, `Farming`, `Scavenging`, `Blacksmithing`, `Carpentry`,
`Cooking`, `Tailoring`, `Engineering`). Lives inside the `Characters` module — skills only ever relate
to a character. Intended as the dependency a future Recipes/crafting feature gates on.

XP is earned by reporting *semantic actions* (`MinedOreDeposit`, `ForgedIngot`, ...), not raw XP
amounts — the backend owns the action→XP-reward mapping (`GET /api/skills` doesn't list actions, only
skills; the action catalog is internal), so game balance can change without a Bridge/mod redeploy. A
separate staff-only endpoint grants raw XP directly, for corrections.

**Bridge integration note:** repeated occurrences of the same action should be coalesced into the
`Quantity` field and flushed periodically (same buffering approach the Bridge already uses for raw
telemetry), not reported once per micro-action — this is what keeps the number of XP-grant events
manageable over a server's lifetime.

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and an existing `$CHARACTER_ID` (see
[Characters](./characters.md)). `POST .../skills/actions` and `GET .../characters/{id}/skills` need
the `gameserver:skills:write` scope (also granted to `gameserver-dev`); `POST .../skills/xp` needs the
staff-only `gameserver:skills:manage` scope, not granted to any Bridge client.

```sh
curl -X POST http://localhost:5100/api/characters/$CHARACTER_ID/skills/actions \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"action":"MinedOreDeposit","quantity":3}'

curl http://localhost:5100/api/characters/$CHARACTER_ID/skills \
  -H "Authorization: Bearer $BRIDGE_TOKEN"

curl http://localhost:5100/api/skills \
  -H "Authorization: Bearer $BRIDGE_TOKEN"
```

## Staff corrections

`POST .../skills/xp` grants XP directly for a named `SkillType`, bypassing the action catalog. Requires
a staff/admin token carrying `gameserver:skills:manage` (e.g. via `staff-admin-dev` or
`eliferpg-portal`), not a Bridge token:

```sh
curl -X POST http://localhost:5100/api/characters/$CHARACTER_ID/skills/xp \
  -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" \
  -d '{"skill":"Cooking","amount":500}'
```

## Errors and limits

- `404` — unknown `$CHARACTER_ID` on any of the per-character endpoints (`POST .../skills/actions`,
  `POST .../skills/xp`, `GET .../skills`).
- `400` — unrecognized `action` string on `POST .../skills/actions`, or unrecognized `skill` string on
  `POST .../skills/xp`.

Levels cap at 100, on a gentle-exponential XP curve (each level costs ~5% more XP than the last); a
skill at the cap reports `xpForNextLevel: 0`.
