# Companies

Companies, membership, and (seeded) positions/permissions. See [MIGRATION.md §8](../MIGRATION.md#8-migration-plan-feature-4--companies).

Company data is hive-wide — reads (`GET /api/companies`, `GET /api/companies/{id}`) now require the `gameserver:companies:write` scope too (previously unscoped), and a company created via one gameserver is visible from every other gameserver in the same deployment.

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and a `characterId` from [Characters](./characters.md) to found the company.

`POST /api/companies` (create a company; the founder becomes its first member, in the "Owner" position) and `POST /api/companies/{id}/members` (add another member, defaulting to "Rookie") need the `gameserver:companies:write` scope (also granted to `gameserver-dev`):

```sh
curl -X POST http://localhost:5100/api/companies \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Acme Corp","founderCharacterId":"<characterId from the create-character response>"}'
```

See [Banking](./banking.md#corporate-accounts) for what a company's "Owner" vs. "Rookie" position controls on a Corporate bank account.
