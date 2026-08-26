# Phone

A device platform. Texting and the address book are the first two **apps** on it; banking, company
management or a camera would be later ones, and none of them should need the platform reworked. See
[MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan.

Phone data is hive-wide: a number reaches its owner regardless of which gameserver they are on, the
same model [Shops](./shops.md) and the whitelist moved to on 2026-08-22. Nothing here carries a
`GameServerId`.

## The device / SIM split

The one idea worth internalising before reading anything else:

| Lives on the **SIM card** | Lives on the **handset** |
| --- | --- |
| The phone number | Which model it is, and therefore what it can do |
| The contact book | Which apps are installed |
| Message threads and history | Whether it is powered on |
| The blocklist | Which SIMs are seated in it |

Move a SIM to another handset and the number, contacts and every conversation come with it; the old
handset keeps nothing. A handset is a host that supplies power, a capability tier and apps.

Two consequences that surprise people:

- **Retention is the handset's, history is the SIM's.** A thread is trimmed to the
  `threadMessageLimit` of whatever device the SIM currently sits in, applied when a message arrives.
  Drop a smartphone SIM into a burner and the next message costs you the backlog. The limit that
  applied rides on each event, so replaying a stream rebuilds exactly the history that existed.
- **Both halves are bound to a character.** The handset is biolocked (`boundCharacterId`, set once at
  provisioning and never changed) and the SIM is registered to a character. Every mutating call
  checks the acting character against *both*, so neither a stolen handset nor a stolen SIM is worth
  anything.

## Apps

`AppCatalog` in `Phone.Domain/Apps` is the backend's list of what apps exist, so adding or
rebalancing one needs no mod redeploy — the same reasoning [Skills](./skills.md) applies to its
action-to-XP map. A `PhoneModel` declares which apps it supports, which is where tier actually bites:
a burner refuses what a smartphone advertises.

Every app command runs one shared guard chain, `PhoneAccessPolicy`:

1. The SIM exists and is `Active` (not suspended, not deactivated).
2. The acting character is registered to that SIM.
3. The SIM is seated in a handset.
4. The acting character is the handset's biolocked owner.
5. The handset is powered on.
6. The app is installed on the handset and supported by its model.

Adding an app buys all six for the cost of one call. See [Adding an app](#adding-an-app).

## Authorization

Like the rest of eliferpg-core, this module **never authorizes gameplay mutations off JWT identity**.
The acting `characterId` is an explicit field on the request and is checked against stored ownership.
That is also why the NPC simulation can drive a phone later through these exact endpoints, with no
parallel path and no "is this a real player" branch anywhere.

Scopes:

| Scope | Covers |
| --- | --- |
| `phone:read` | Reading models, handsets, SIMs, contacts and threads |
| `phone:write` | Everything a character does with their own phone, plus the SignalR hub |
| `phone:provision` | Creating handsets and SIMs — the gameserver bridge, and later the NPC service |
| `phone:manage` | The model catalog and the staff moderation reads |
| `phone:enforce` | Suspending and restoring a SIM |

`phone:enforce` is deliberately its own scope rather than part of `phone:manage`, so an in-game
Police/State faction can be granted exactly that later without also gaining catalog and moderation
powers.

## Walkthrough

Needs `$BRIDGE_TOKEN` (see [Accounts](./accounts.md)) and a `characterId` from
[Characters](./characters.md).

```sh
MODEL_ID=$(curl -s -X POST http://localhost:5100/api/phone-models \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d '{"displayName":"Burner","tier":1,"simSlots":1,"supportedApps":["Messages","Contacts"],
       "contactLimit":20,"threadMessageLimit":30,"maxGroupParticipants":5}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['modelId'])")

PHONE_ID=$(curl -s -X POST http://localhost:5100/api/phones \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"modelId\":\"$MODEL_ID\"}" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['phoneId'])")

# A handset ships with its model's apps installed, and powered off.
SIM=$(curl -s -X POST http://localhost:5100/api/sim-cards \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\"}")
SIM_ID=$(echo "$SIM" | python3 -c "import json,sys; print(json.load(sys.stdin)['simCardId'])")
NUMBER=$(echo "$SIM" | python3 -c "import json,sys; print(json.load(sys.stdin)['number'])")

curl -s -X PUT http://localhost:5100/api/phones/$PHONE_ID/sims/$SIM_ID \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\"}"

curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/power \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"isPoweredOn\":true}"

curl -s -X POST http://localhost:5100/api/sim-cards/$SIM_ID/messages \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"characterId\":\"$CHARACTER_ID\",\"to\":[\"$OTHER_NUMBER\"],\"body\":\"on my way\"}"
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
| Unknown number, or SIM `Deactivated` | Undeliverable, reported back to the sender |
| SIM `Suspended` | Undeliverable, reported back — and **not queued** |
| Sender's number is on the recipient's blocklist | Dropped silently, **not** reported |
| SIM loose, handset off, or Messages uninstalled | Queued as a `PendingDelivery` |
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

Queued messages are delivered when the number becomes reachable again: powering the handset on, or
seating the SIM. Both are safe to repeat — a still-unreachable SIM simply leaves everything queued,
and each delivery leaves the queue in the same commit that appends it to the thread.

## Threads

A thread is keyed by *(SIM, participant set)*. There is no group object to create, name or
administer — addressing two people simply lands in the thread for those two people, and addressing
them again in the other order lands in the same one. From a recipient's side the thread is "everyone
else", meaning the sender plus the other recipients.

## Rate limiting

Per SIM, not per handset: the number is the sending identity. The limit is
`hiveSettings.smsPerMinutePerSim`, alongside `smsMaxBodyLength` — both editable at runtime through
`PATCH /api/hive/settings`. It is a fixed window, so a burst spanning a boundary can reach twice the
limit; that is well within what this throttle is for.

## Real-time

`hubs/phone`, token via the `access_token` query parameter, same as [Shops](./shops.md).

- `SubscribeToSim(simCardId)` / `UnsubscribeFromSim(simCardId)`, with `?characterId=` on the
  connection.
- **Subscribing authorizes**, unlike `ShopsHub`: shops are hive-public, message threads are not.
- Groups are keyed by SIM, so a subscription survives the SIM moving to another handset, and one
  subscription carries every app's events.
- Events: `MessageReceived`, `ThreadUpdated`.
- As with Shops, the hub is a delivery convenience and **never the source of truth**. Re-fetch on
  reconnect.

## Adding an app

1. One `AppKey` member and one `AppDefinition` in `AppCatalog`.
2. `Apps/<Name>/` folders in `Phone.Domain`, `Phone.Application` and `Phone.Api`.
3. Add the key to the relevant `PhoneModel.supportedApps` — data, not code.
4. Call `PhoneAccessPolicy` with the new key and inherit SIM status, SIM ownership, biolock, power
   state and the install check unchanged.
5. New hub event names on the existing per-SIM group.

Nothing under `Devices/` or `Sims/` is touched. `AppKey` is an **append-only** enum: ordinals are
persisted in Marten payloads, so inserting a member mid-list remaps every stored value.

A Banking app is also where `ICrossModuleTransaction` would finally earn its place in this module,
spanning the Phone and Banking stores the way `PurchaseListingHandler` already spans Shops and
Banking.

## TODO: inventory, and where devices come from

Handsets and SIMs exist **only** through `POST /api/phones` and `POST /api/sim-cards` under
`phone:provision`. They are not yet connected to buying one.

eliferpg-core has no per-character inventory (listed as an unbuilt prerequisite in
`npc-virtual-simulation/docs/concept/ownership-map.md`), so a shop purchase cannot hand over a
device: [Shops](./shops.md) can sell a phone `Item`, but nothing links that purchase to a device
record.

Closing this needs Reforger inventory persistence for **composed items**. A phone is a compound
object: an item instance carrying properties that reference the `phoneId` it is and the `simCardId`
currently seated in it. Once an inventory item can hold that:

- provisioning moves into the purchase flow (see `PurchaseListingHandler`'s
  `ICrossModuleTransaction`), so paying and receiving commit together;
- buying, dropping, looting and trading a handset all become inventory operations rather than
  separate API calls;
- `PhoneModel.itemId` — already on the model, already returned by the API — becomes the link the
  bridge uses to spawn the right prefab.

Until then, the biolock is what makes a dropped or looted handset harmless: possession confers
nothing, because the backend only ever answers to the bound character.
