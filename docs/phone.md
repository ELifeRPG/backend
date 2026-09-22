# Phone

A device platform. Texting and the address book are the first two **apps** on it; banking, company
management or a camera would be later ones, and none of them should need the platform reworked. See
[MIGRATION.md](../MIGRATION.md) for how this fits the overall migration plan. Alongside the apps sits
one platform-level, app-independent notification queue — see [Notifications](#notifications) — so
`Notifications/` in the source tree is not itself an app.

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

- **Limits are hive-wide, not per handset.** `PhoneContactLimit`, `PhoneThreadMessageLimit`,
  `PhoneMaxGroupParticipants` and `PhoneNotificationLimit` live on `hiveSettings` next to
  `smsPerMinutePerPhone` and `smsMaxBodyLength`, and are editable at runtime through
  `PATCH /api/hive/settings`. Retention is applied when a message arrives and the limit that applied
  rides on the event, so replaying a stream rebuilds exactly the history that existed — and lowering
  the cap costs a thread its backlog on its *next* message rather than at once.
  `PhoneNotificationLimit` is a queue depth, not a retention window: it caps what a phone's
  notification queue may hold, oldest dropped first, and is read fresh at publish time rather than
  carried on anything.
- **The PIN replaced the biolock.** A handset used to be bound to one character forever, which made
  a dropped or looted phone a brick. Now possession plus the PIN is enough. See below.

## The PIN

Every phone is provisioned with a PIN: 4 to 8 digits, since it is typed on an in-game keypad.

**The PIN is checked once, at power-on, not on every call.** `SetPhonePowerCommand` runs
`PhoneAccessPolicy.IsAuthorized` — the registered owner outright, or the PIN from anyone else
holding the handset — and every app operation then requires the phone to be powered on. Re-checking
the actor per call would only re-prove what the power state already carries, so app operations name
no acting character at all: they address a phone, and a powered-on phone is a usable phone.

The five platform commands that establish or change possession still take a `characterId`, and a
`pin` when the caller is not the owner: provision, power, PIN change, and app install/uninstall.
`POST /api/phones/{phoneId}/pin` takes the owner or the current PIN — so whoever picks up a phone and
knows the PIN can lock the previous owner out.

The trade-off, recorded deliberately: `IsPoweredOn` is durable rather than session-scoped, so it
means "someone unlocked this at some point", not "just now". The in-game lock screen is the real
gate — the mod owns it, and the guard chain below protects the device's own state rather than
policing who is looking at the screen.

Enforcement (`phone:enforce`, or `gameserver:phone:enforce` for a Bridge) is the one thing the PIN
does not open: suspend and restore take no acting character and no PIN at all, because the point of
an enforcement action is that the holder does not consent to it.

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

`PhoneAccessPolicy` is three chains now, not two, each built for a different kind of surface:

1. **The app chain** — every app command runs this. The phone exists; it is `Active` (not suspended,
   not deactivated); it is powered on; the named app is installed. Adding an app buys all four for
   the cost of one call. See [Adding an app](#adding-an-app).
2. **The device chain** — the app chain minus the install step, for platform surfaces that belong to
   the handset rather than to anything running on it. The notification queue is the first of these:
   a notification is reachable with every app uninstalled, and refused only when the phone itself is
   not usable. The app chain is a thin wrapper over this one, adding only the install check.
3. **The possession check**, `PhoneAccessPolicy.IsAuthorized` — ownership-or-PIN, used by the five
   platform commands (provision, power, PIN change, app install/uninstall) instead of either chain
   above. It checks neither power nor an installed app, deliberately: you cannot require a phone to
   be switched on in order to switch it on. This is where possession is proven, and the other two
   chains lean on it having already run.

There is no ownership step on the app or device chains: being powered on already implies one, since
powering the phone on is where the possession check ran.

Everything an app owns is rooted under `/api/phones/{phoneId}/apps/{appKey}/`, mirroring the
`Apps/<Name>/` folders in Domain, Application and Api. A new app owns that prefix outright, so two
apps can never race each other for the same noun. The blocklist is under Messages for that reason:
it is one app's list, and its URL and its guard chain agree about which.

## Notifications

A platform-level, app-independent banner queue — the APNs/FCM idea, grafted onto a phone that has no
OS of its own. `PhoneNotification` in `Phone.Domain/Notifications` is a plain document, not an
event: like `PendingDelivery`, this is delivery state rather than history worth replaying, and it is
**immutable once written** — nothing is ever updated in place, only stored or deleted whole. That is
what lets a client ack a batch of ids with no version field to race.

| iOS/Android | Here |
| --- | --- |
| One OS-level push channel; apps do not each hold a connection | `GET`/`POST /api/phones/{phoneId}/notifications*`, outside `/apps/` |
| Routed by device token + app id | `phoneId` + `AppKey` |
| Alert payload renders without the OS knowing the app | Fat payload: title, body, category, ready to render with no second call |
| `category` picks icon/sound within an app | `Category`, a free-form string — `"message.received"` today |
| `thread-id` groups banners ("3 from Jane") | `GroupKey` — the recipient's own thread id, for Messages |
| Badge is app-owned, set explicitly | `UnreadCount` / `MarkThreadRead`, untouched by any of this |
| Push is best-effort; the app's own sync is the truth | The queue is delivery; `GET .../threads/{id}` stays truth |

- **Explicit ack, not implicit-on-read.** `GET /api/phones/{phoneId}/notifications` never removes
  anything on its own — a lost response must not lose a notification. `POST
  /api/phones/{phoneId}/notifications/ack` with `{"ids": [...]}` is what deletes them, and it is
  idempotent: an id already gone (already acked, already trimmed by the cap) is silently ignored
  rather than reported, so a caller unsure whether an earlier ack landed can just resend it.
- **`?appKey=` filters** a `GET`, using the same `AppKey` values as everywhere else in this module.
- **Reading a thread clears its banners; acking one does not.** `MarkThreadReadCommand` deletes that
  thread's queued notifications (matched on `GroupKey`) in the same commit that clears
  `UnreadCount` — the real-phone rule. Acking a poll only removes rows the caller named; it says "I
  saw this banner", not "I opened the conversation", and the two are deliberately independent (see
  [Threads](#threads) for the message-side version of the same split).
- **The cap drops oldest first**, no TTL — `hiveSettings.PhoneNotificationLimit`, checked at publish
  time. This is the "we do not accumulate a backlog" property APNs has, not a retention policy.
- **The guard chain is the device half of `PhoneAccessPolicy`**, not the app half — see
  [Apps](#apps). A notification is reachable with the publishing app uninstalled, refused only when
  the phone itself is not usable: a powered-off phone's queue is `409`, a suspended one `403`.
- **Publication is the publishing app's job**, on the same `IPhoneSession` as whatever caused it, so
  the notification commits atomically with the change it describes. The platform never composes one
  itself and stores no opinion about what any app's `Category` or `Payload` mean.
- **Titles carry the bare sender number, not a resolved name.** Messages deliberately does not read
  Contacts (see [Threads](#threads)) — a banner is not a reason to reverse that, so the mod resolves
  a display name itself, the same call it already makes to draw a thread.

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

Notifications       GET    /api/phones/{phoneId}/notifications
                    POST   /api/phones/{phoneId}/notifications/ack

Contacts app        GET    /api/phones/{phoneId}/apps/contacts/entries
                    POST   /api/phones/{phoneId}/apps/contacts/entries
                    PATCH  /api/phones/{phoneId}/apps/contacts/entries/{contactId}
                    DELETE /api/phones/{phoneId}/apps/contacts/entries/{contactId}

Messages app        GET    /api/phones/{phoneId}/apps/messages/threads
                    GET    /api/phones/{phoneId}/apps/messages/threads/{threadId}
                    GET    /api/phones/{phoneId}/apps/messages/updates
                    POST   /api/phones/{phoneId}/apps/messages/updates/ack
                    POST   /api/phones/{phoneId}/apps/messages/threads/{threadId}/read
                    POST   /api/phones/{phoneId}/apps/messages/send
                    POST   /api/phones/{phoneId}/apps/messages/blocks
                    DELETE /api/phones/{phoneId}/apps/messages/blocks/{number}

Staff               GET    /api/admin/phones
                    GET    /api/admin/phones/{phoneId}/threads
```

`send` is a verb rather than a POST to a collection on purpose: a send is not the creation of one
thing, it fans out across the sender's thread and every reachable recipient's. `updates` no longer
takes a `since` query parameter — see [Polling, for clients without a socket](#polling-for-clients-without-a-socket).

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
| `phone:enforce` | Suspending and restoring a phone — staff |
| `gameserver:phone:enforce` | The same, for a Bridge acting on an in-game faction's behalf |

They follow the realm's split: `gameserver:<module>:<verb>` for what a gameserver's Bridge holds,
a bare `<x>:<verb>` for staff, the same as `accounts:manage` and `inventory:manage`. They were bare
`phone:*` until this module lost its SIM, but had never been registered in
`infra/keycloak/eliferpg-realm.json` at all — no token could carry them, so nothing was in use to
break. They are registered now, on `gameserver-dev` and `staff-admin-dev` respectively.

`phone:enforce` is deliberately its own scope rather than part of `phone:manage`, so an in-game
Police/State faction can be granted exactly that later without also gaining moderation powers.

The notification routes need no scope of their own: `gameserver:phone:read` and
`gameserver:phone:write` already cover them, and `infra/keycloak/eliferpg-realm.json` did not
change for this — a `gameserver:phone:notify` scope would be redundant, not an oversight.

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

# App operations name no acting character: powering the phone on is where possession was proven.
curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/apps/messages/send \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"to\":[\"$OTHER_NUMBER\"],\"body\":\"on my way\"}"

# Polling for what arrived. There is no cursor to hold onto any more — retrieval is a
# server-tracked watermark per thread, advanced explicitly by acking what you actually processed.
UPDATES=$(curl -s "http://localhost:5100/api/phones/$PHONE_ID/apps/messages/updates" \
  -H "Authorization: Bearer $BRIDGE_TOKEN")
THREAD_ID=$(echo "$UPDATES" | python3 -c "import json,sys; print(json.load(sys.stdin)['threads'][0]['id'])")
HIGHEST=$(echo "$UPDATES" | python3 -c "import json,sys; print(json.load(sys.stdin)['threads'][0]['highestSequence'])")

curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/apps/messages/updates/ack \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"threads\":[{\"threadId\":\"$THREAD_ID\",\"throughSequence\":$HIGHEST}]}"

# The same message also posted a notification, app-independent of Messages — reachable through the
# platform queue rather than /apps/messages/.
NOTIFICATIONS=$(curl -s "http://localhost:5100/api/phones/$PHONE_ID/notifications" \
  -H "Authorization: Bearer $BRIDGE_TOKEN")
NOTIFICATION_ID=$(echo "$NOTIFICATIONS" | python3 -c "import json,sys; print(json.load(sys.stdin)['notifications'][0]['id'])")

curl -s -X POST http://localhost:5100/api/phones/$PHONE_ID/notifications/ack \
  -H "Authorization: Bearer $BRIDGE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"ids\":[\"$NOTIFICATION_ID\"]}"
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

**Every append to a recipient's thread also posts a notification**, in that same commit — see
[Notifications](#notifications). Undeliverable, queued and blocked recipients get none: a
notification is a consequence of an append, and none of those three append anything.

## Threads

A thread is keyed by *(phone, participant set)*. There is no group object to create, name or
administer — addressing two people simply lands in the thread for those two people, and addressing
them again in the other order lands in the same one. From a recipient's side the thread is "everyone
else", meaning the sender plus the other recipients. Threads store bare numbers; resolving a display
name is the Contacts app's job, on the client — Messages deliberately does not read Contacts, and a
notification's title does not either, for the same reason (see [Notifications](#notifications)).

Every message carries a per-thread `Sequence`: an ever-increasing counter, not the message's position
in the retained list. It has to be a counter rather than an index because retention trims the front
of that list — an index would be reused by whatever slides into the gap, silently colliding with
history. `RetrievedThrough` is the high-water sequence a poller has told the thread it retrieved.

**Retrieved and read are deliberately independent axes.** Retrieved means the mod pulled the content
down — advanced only by acking `GET .../apps/messages/updates` (see
[Polling](#polling-for-clients-without-a-socket)). Read means the player opened the thread — the only
thing `POST .../threads/{threadId}/read` still does, exactly as before. Acking a poll never touches
`UnreadCount`, and marking a thread read never touches `RetrievedThrough`: a background poll must not
silently clear a player's unread badge, and opening a thread is not the same fact as a sync finishing.

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
- Events: `MessageReceived`, `ThreadUpdated`, `NotificationPosted`.
- As with Shops, the hub is a delivery convenience and **never the source of truth**. Re-fetch on
  reconnect.
- `NotificationPosted` is pushed from the send path only, not from the power-on flush that delivers a
  queued backlog. The flush pushes nothing over this hub today, and wiring it in would mean threading
  a result through three more layers to feed a hub that is never the source of truth anyway, for a
  phone that is about to `GET /notifications` as part of booting regardless.

### Polling, for clients without a socket

ArmA Reforger has no SignalR client, so the gameserver Bridge cannot hold a hub connection. It polls
two independent surfaces instead, both delivery rather than truth:

**Messages** — `GET /api/phones/{phoneId}/apps/messages/updates` reports every message this phone has
not yet retrieved. There is no cursor to hold: retrieval is a server-tracked watermark per thread
(see [Threads](#threads)), advanced only by `POST /api/phones/{phoneId}/apps/messages/updates/ack`
with the per-thread sequence you actually finished processing.

- Delivery is at-least-once — dedupe on message id. A half-processed batch is safe to ack partially:
  a thread you have not finished with is simply left off the ack, and re-sending the same ack twice
  is a no-op.
- It runs the same guard chain as any other Messages operation, so a powered-off phone polls `409`
  and a suspended one `403`.
- Polling marks nothing — neither read nor retrieved. `POST .../threads/{threadId}/read` remains the
  only thing that clears an unread count, and only the ack above advances `RetrievedThrough`.
- Retention still applies: a message can be trimmed before a slow poller sees it, which is why the
  hub's rule holds here too — this is delivery, and `GET .../threads/{threadId}` is the truth.
- Acking requires the phone powered on, like any Messages operation. A power-cycle between polls
  therefore re-delivers whatever the last poll returned but never got acked — at-least-once, the safe
  direction, not a bug.

**Notifications** — `GET /api/phones/{phoneId}/notifications` and its `ack`, covered in
[Notifications](#notifications). A separate surface on purpose: it is app-independent platform state,
not something Messages owns, even though Messages is the only publisher today.

## Adding an app

1. One `AppKey` member and one `AppDefinition` in `AppCatalog`.
2. `Apps/<Name>/` folders in `Phone.Domain`, `Phone.Application` and `Phone.Api`.
3. Routes under `/api/phones/{phoneId}/apps/<key>/`, which is yours alone — no coordination with
   any other app about names.
4. Call `PhoneAccessPolicy` with the new key and inherit phone status, ownership-or-PIN, power state
   and the install check unchanged.
5. New hub event names on the existing per-phone group.
6. Optionally, publish into the notification queue through `IPhoneNotificationRepository` on the
   same session as whatever caused it — your own `Category`, your own `GroupKey` (the client-side
   grouping key for your app's banners), and whatever `Payload` your app's clients need to act on
   one. See [Notifications](#notifications) for the contract you are expected to honour: immutable
   once posted, no display names resolved server-side, and respect `PhoneNotificationLimit`.

Nothing under `Devices/` is touched. `AppKey` is an **append-only** enum: ordinals are persisted in
Marten payloads, so inserting a member mid-list remaps every stored value — and it now reaches two
document types, `PhoneDevice`'s installed-app list and every `PhoneNotification`, so the blast radius
of breaking that rule is wider than it used to be.

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
