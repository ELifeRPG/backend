# Phone

A device platform. Texting and the address book are the first two **apps** on it; banking, company
management or a camera would be later ones, and none of them should need the platform reworked. See
[MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan.

Phone data is hive-wide: a number reaches its owner regardless of which gameserver they are on, the
same model [Shops](./shops.md) and the whitelist moved to on 2026-08-22. Nothing here carries a
`GameServerId`.

## One phone, one number

A phone is a single thing. The number, the PIN, the blocklist, the contacts, the message history,
the installed apps and the power state all belong to the handset, and a character may hold several
handsets, each with its own number.

This replaces an earlier device/SIM split, where a transferable `SimCard` owned the identity and the
handset was only a host supplying power, apps and a capability tier. Nothing could exercise it —
handsets and SIMs only ever appear through provisioning, so nobody could move a card between
two handsets they did not have — and it cost every app command a two-aggregate guard chain. The
tiering went with it: there is no `PhoneModel` catalog any more, and every phone has the same limits.

Two consequences worth internalising:

- **Limits are hive-wide, not per handset.** `PhoneContactLimit`, `PhoneThreadMessageLimit` and
  `PhoneMaxGroupParticipants` live on `hiveSettings` next to `smsPerMinutePerPhone` and
  `smsMaxBodyLength`, and are editable at runtime through `PATCH /api/hive/settings`. Retention is
  applied when a message arrives and the limit that applied rides on the event, so replaying a
  stream rebuilds exactly the history that existed — and lowering the cap costs a thread its backlog
  on its *next* message rather than at once.
- **The PIN replaced the biolock.** A handset used to be bound to one character forever, which made
  a dropped or looted phone a brick. Now possession plus the PIN is enough. See below.

## The PIN

Every phone is provisioned with a PIN: 4 to 8 digits, since it is typed on an in-game keypad.

The owner never sends it. `PhoneAccessPolicy` grants the registered owner outright, so the mod fills
it in implicitly for the character who owns the handset; `pin` is an optional field that anyone
*else* holding the phone supplies. `POST /api/phones/{phoneId}/pin` changes it, and takes the owner
or the current PIN — so whoever picks up a phone and knows the PIN can lock the previous owner out.

Enforcement (`phone:enforce`) is the one thing the PIN does not open: suspend and restore take no
acting character and no PIN at all, because the point of an enforcement action is that the holder
does not consent to it.

Three deliberate choices, recorded so they are not re-litigated:

- **Stored in the clear.** It is a game prop, not a credential. The only caller is the Bridge holding
  a client-credentials token, so there is no untrusted party on the other end, and hashing four
  digits would not stop anyone who can already reach the endpoint.
- **No attempt counter and no lockout.** For the same reason — the mod owns the in-game attempt UX.
- **Never returned by any read**, moderation reads included. A read endpoint that echoed it would
  hand every holder of `gameserver:phone:read` the key to every handset.

It does travel in the query string on the two `DELETE` routes that take one (uninstalling an app,
unblocking a number), so it reaches request logs. That is accepted rather than overlooked: it is
already stored in the clear, so a log line exposes nothing the database does not, and the
alternative is a `DELETE` with a body — which every other delete in this codebase avoids.

## Apps

`AppCatalog` in `Phone.Domain/Apps` is the backend's list of what apps exist, so adding or
rebalancing one needs no mod redeploy — the same reasoning [Skills](./skills.md) applies to its
action-to-XP map. Every phone can run every entry: with models gone, installing an app is a player's
choice rather than a permission the handset grants. A phone ships with all of them installed.

What installing still governs is delivery. Uninstalling Messages does not lose anything — contacts
and threads belong to the phone — and incoming messages queue rather than vanish, arriving when it
is installed again.

Every app command runs one shared guard chain, `PhoneAccessPolicy`:

1. The phone exists.
2. The acting character is the registered owner, **or** the supplied PIN matches.
3. The phone is `Active` (not suspended, not deactivated).
4. The phone is powered on, and the app is installed.

Adding an app buys all four for the cost of one call. See [Adding an app](#adding-an-app).

Platform commands deliberately run less than the whole chain. Power, apps and the PIN itself need
step 1 and 2 but not power or an installed app — you cannot require a phone to be switched on in
order to switch it on.

Everything an app owns is rooted under `/api/phones/{phoneId}/apps/{appKey}/`, mirroring the
`Apps/<Name>/` folders in Domain, Application and Api. A new app owns that prefix outright, so two
apps can never race each other for the same noun. The blocklist is under Messages for that reason:
it is one app's list, and its URL and its guard chain agree about which.

## Routes

The split the URLs make visible: the phone itself, versus what runs on it.

```
Platform            POST   /api/phones
                    GET    /api/phones/{phoneId}
                    GET    /api/characters/{characterId}/phones
                    POST   /api/phones/{phoneId}/power
                    POST   /api/phones/{phoneId}/pin
                    GET    /api/phones/{phoneId}/apps
                    PUT    /api/phones/{phoneId}/apps/{appKey}
                    DELETE /api/phones/{phoneId}/apps/{appKey}

Enforcement         POST   /api/phones/{phoneId}/suspend
                    POST   /api/phones/{phoneId}/restore

Contacts app        GET    /api/phones/{phoneId}/apps/contacts/entries
                    POST   /api/phones/{phoneId}/apps/contacts/entries
                    PATCH  /api/phones/{phoneId}/apps/contacts/entries/{contactId}
                    DELETE /api/phones/{phoneId}/apps/contacts/entries/{contactId}

Messages app        GET    /api/phones/{phoneId}/apps/messages/threads
                    GET    /api/phones/{phoneId}/apps/messages/threads/{threadId}
                    POST   /api/phones/{phoneId}/apps/messages/threads/{threadId}/read
                    POST   /api/phones/{phoneId}/apps/messages/send
                    POST   /api/phones/{phoneId}/apps/messages/blocks
                    DELETE /api/phones/{phoneId}/apps/messages/blocks/{number}

Staff               GET    /api/admin/phones
                    GET    /api/admin/phones/{phoneId}/threads
```

`send` is a verb rather than a POST to a collection on purpose: a send is not the creation of one
thing, it fans out across the sender's thread and every reachable recipient's.

## Authorization

Like the rest of eliferpg-core, this module **never authorizes gameplay mutations off JWT identity**.
The acting `characterId` is an explicit field on the request and is checked against stored ownership.
That is also why the NPC simulation can drive a phone later through these exact endpoints, with no
parallel path and no "is this a real player" branch anywhere.

Scopes:

| Scope | Covers |
| --- | --- |
| `gameserver:phone:read` | Reading phones, contacts and threads |
| `gameserver:phone:write` | Everything a character does with a phone, plus the SignalR hub |
| `gameserver:phone:provision` | Creating phones — the gameserver bridge, and later the NPC service |
| `phone:manage` | The staff moderation reads |
| `phone:enforce` | Suspending and restoring a phone |

They follow the realm's split: `gameserver:<module>:<verb>` for what a gameserver's Bridge holds,
a bare `<x>:<verb>` for staff, the same as `accounts:manage` and `inventory:manage`. They were bare
`phone:*` until this module lost its SIM, but had never been registered in
`infra/keycloak/eliferpg-realm.json` at all — no token could carry them, so nothing was in use to
break. They are registered now, on `gameserver-dev` and `staff-admin-dev` respectively.

`phone:enforce` is deliberately its own scope rather than part of `phone:manage`, so an in-game
Police/State faction can be granted exactly that later without also gaining moderation powers.

## Walkthrough

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and a `characterId` from
[Characters](./characters.md).

```sh
PHONE=$(curl -s -X POST http://localhost:5100/api/phones \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"pin\":\"1234\"}")
PHONE_ID=$(echo "$PHONE" | python3 -c "import json,sys; print(json.load(sys.stdin)['phoneId'])")
NUMBER=$(echo "$PHONE" | python3 -c "import json,sys; print(json.load(sys.stdin)['number'])")

# A phone ships with every app installed, and powered off.
curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/power \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"isPoweredOn\":true}"

curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/apps/messages/send \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"to\":[\"$OTHER_NUMBER\"],\"body\":\"on my way\"}"

# Someone else holding the handset sends the PIN alongside their own characterId.
curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/apps/messages/send \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$OTHER_CHARACTER_ID\",\"pin\":\"1234\",\"to\":[\"$OTHER_NUMBER\"],\"body\":\"borrowed this\"}"
```

Numbers are eight digits. They are typed by hand in game, so the API accepts spaces, dashes,
parentheses and a leading `+`, and canonicalises before doing anything — two spellings of the same
number must not key two different threads.

## What happens to a message

Send fans out across the sender's thread and every reachable recipient's, all on one Marten session,
so a single commit covers the lot. A message present in the sender's history but in nobody's inbox is
the outcome this flow exists to prevent.

Per recipient:

| Recipient state | Outcome |
| --- | --- |
| Unknown number, or phone `Deactivated` | Undeliverable, reported back to the sender |
| Phone `Suspended` | Undeliverable, reported back — and **not** queued |
| Sender's number is on the recipient's blocklist | Dropped silently, **not** reported |
| Phone powered off, or Messages uninstalled | Queued as a `PendingDelivery` |
| Otherwise | Appended to the recipient's thread |

Two of those are deliberate and easy to get wrong later:

- **Blocking is invisible to the sender.** They see a delivered message with nothing undeliverable,
  because that is what blocking looks like from the outside. Reporting it would make the API a
  block-detector.
- **Suspension blocks rather than delays.** Nothing is held for a later restore; holding it would
  turn an enforcement action into a delay. Nothing already stored is lost, though — contacts,
  threads and the blocklist all survive a suspend/restore cycle intact.

The sender's own thread is appended regardless. Texting a dead, blocked or suspended number still
reads as sent from their side, exactly like SMS.

Queued messages are delivered when the number becomes reachable again: powering the phone on, or
installing Messages. Both are safe to repeat — a still-unreachable phone simply leaves everything
queued, and each delivery leaves the queue in the same commit that appends it to the thread.

## Threads

A thread is keyed by *(phone, participant set)*. There is no group object to create, name or
administer — addressing two people simply lands in the thread for those two people, and addressing
them again in the other order lands in the same one. From a recipient's side the thread is "everyone
else", meaning the sender plus the other recipients.

## Rate limiting

Per phone, which is per number since the two are now one thing. The limit is
`hiveSettings.smsPerMinutePerPhone`, alongside `smsMaxBodyLength` — both editable at runtime through
`PATCH /api/hive/settings`. It is a fixed window, so a burst spanning a boundary can reach twice the
limit; that is well within what this throttle is for.

## Real-time

`hubs/phone`, token via the `access_token` query parameter, same as [Shops](./shops.md).

- `SubscribeToPhone(phoneId)` / `UnsubscribeFromPhone(phoneId)`, with `?characterId=` on the
  connection.
- **Subscribing authorizes**, unlike `ShopsHub`: shops are hive-public, message threads are not. It
  takes *ownership*, not the PIN — a live subscription is a standing grant rather than a single act,
  so it is deliberately narrower than what the guard chain allows a borrower to do.
- Groups are keyed by phone, so one subscription carries every app's events.
- Events: `MessageReceived`, `ThreadUpdated`.
- As with Shops, the hub is a delivery convenience and **never the source of truth**. Re-fetch on
  reconnect.

## Adding an app

1. One `AppKey` member and one `AppDefinition` in `AppCatalog`.
2. `Apps/<Name>/` folders in `Phone.Domain`, `Phone.Application` and `Phone.Api`.
3. Routes under `/api/phones/{phoneId}/apps/<key>/`, which is yours alone — no coordination with
   any other app about names.
4. Call `PhoneAccessPolicy` with the new key and inherit phone status, ownership-or-PIN, power state
   and the install check unchanged.
5. New hub event names on the existing per-phone group.

Nothing under `Devices/` is touched. `AppKey` is an **append-only** enum: ordinals are persisted in
Marten payloads, so inserting a member mid-list remaps every stored value.

A Banking app is also where `ICrossModuleTransaction` would finally earn its place in this module,
spanning the Phone and Banking stores the way `PurchaseListingHandler` already spans Shops and
Banking.

## TODO: inventory, and where phones come from

Phones exist **only** through `POST /api/phones` under `gameserver:phone:provision`. They are not
yet connected to buying one.

eliferpg-core has no per-character inventory (listed as an unbuilt prerequisite in
`npc-virtual-simulation/docs/concept/ownership-map.md`), so a shop purchase cannot hand over a
device: [Shops](./shops.md) can sell a phone `Item`, but nothing links that purchase to a phone
record.

Closing this needs Reforger inventory persistence: an item instance carrying a property that
references the `phoneId` it is. Once an inventory item can hold that:

- provisioning moves into the purchase flow (see `PurchaseListingHandler`'s
  `ICrossModuleTransaction`), so paying and receiving commit together;
- buying, dropping, looting and trading a handset all become inventory operations rather than
  separate API calls.

That is also the point at which the PIN starts to matter in earnest — until a phone can change hands
in-world, nobody but its owner can be holding one.
