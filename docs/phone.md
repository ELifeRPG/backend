# Phone

A device platform. Seven **apps** run on it: the dialer, texting, the address book, settings and the
store are the phone's own software, and Bank and Map are installed from the store. A camera or
company management would be later ones, and none of them should need the platform reworked. See
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
  carried on anything. `PhoneRingSeconds` is the third shape again: carried on `CallPlaced` like a
  retention limit, because whether a given call has rung out is a fact about the window it was placed
  under — lowering it must not retroactively miss calls already in progress.
- **The PIN replaced the biolock.** A handset used to be bound to one character forever, which made
  a dropped or looted phone a brick. Now possession plus the PIN is enough. See below.

## The PIN

Every phone is provisioned with a PIN: 4 to 8 digits, since it is typed on an in-game keypad.

**The PIN is checked once, at power-on, not on every call.** `SetPhonePowerCommand` runs
`PhoneAccessPolicy.IsAuthorized` — the registered owner outright, or the PIN from anyone else
holding the handset — and every app operation then requires the phone to be powered on. Re-checking
the actor per call would only re-prove what the power state already carries, so app operations name
no acting character at all: they address a phone, and a powered-on phone is a usable phone.

The platform commands that establish or change possession still take a `characterId`, and a
`pin` when the caller is not the owner: provision, power, PIN change, preferences, and app install/uninstall.
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

## Preferences

A phone's home screen layout, wallpaper and Do Not Disturb are stored so a phone rebuilt after a
game server restart comes back as it was left. They belong to the phone, not the player: everyone
looking at it sees the same layout, and Do Not Disturb silences it for everyone nearby.
`GET /api/phones/{phoneId}` carries them too, so a restore is one call.

The backend treats them as opaque. `homeLayout` (at most 1024 characters, the mod's own limit) and
`wallpaper` (a preset id, at most 64) are the mod's formats, stored as-is; empty means "use the
mod's default", which is what a new phone starts with. `layoutVersion` lets the mod migrate its
layout format, so it is sent together with `homeLayout` or not at all.

`PATCH` changes only the fields it is sent, and takes the owner or the PIN like the PIN change does —
and like it, does not need the phone powered on. The read is `gameserver:phone:read` alone, the same
as `GET /api/phones/{phoneId}` that already returns them.

Stored as a plain document per phone rather than events: there is no history worth replaying, and
an event per write would re-record a kilobyte of layout each time. Writes are last-write-wins; the
mod debounces them, and a phone that has never been changed has no document at all.

## Apps

`AppCatalog` in `Phone.Domain/Apps` is the backend's list of what apps exist, so adding or
rebalancing one needs no mod redeploy — the same reasoning [Skills](./skills.md) applies to its
action-to-XP map. Every phone can run every entry: with models gone, installing an app is a player's
choice rather than a permission the handset grants.

One flag divides the catalog. An `AppDefinition` with `IsSystemApp` is the phone's own software:

| | Apps | Ships on a new phone | Can be uninstalled | Offered by the store |
| --- | --- | --- | --- | --- |
| **System** | Settings, Store, Phone, Messages, Contacts | yes | **no** | no |
| **Store** | Bank, Map | no | yes | yes |

Listed in `AppKey` order, which is the order a new phone installs them and the order
`GET .../apps` returns them.

A system app is refused by `PhoneDevice.UninstallApp`: deleting the dialer, the settings or the store
itself would leave a handset nobody could repair from inside the game. **Installing one is still
allowed**, and deliberately — it is the only route by which a phone provisioned before an app existed
ever receives it, and it is idempotent, so a bridge may call it freely.

Uninstalling a store app loses nothing: contacts and threads belong to the phone, not to an app, so
reinstalling brings them straight back.

> **Phone data from before this change has to be wiped, not migrated.** `AppKey` was renumbered to
> leave 0 unassigned, so the ordinals already in Postgres mean something else now: a stored `0`
> (Messages) names no app at all and makes `AppCatalog.Get` throw, and a stored `1` (Contacts) reads
> as `Settings`. Older phones also only ever got `AppInstalled` events for two of the seven apps.
> Drop the schema — `docker exec backend-postgres-1 psql -U postgres -c "DROP SCHEMA IF EXISTS phone
> CASCADE;"`, or `docker compose down -v` for everything. There is no deployed environment, so this
> costs local scratch phones only. A `phone:manage` backfill command would have covered the missing
> apps, but not the renumbering, which is why it is not the answer here.

`PhoneAccessPolicy` is three chains now, not two, each built for a different kind of surface:

1. **The app chain** — every app command runs this. The phone exists; it is `Active` (not suspended,
   not deactivated); it is powered on; the named app is installed. Adding an app buys all four for
   the cost of one call. See [Adding an app](#adding-an-app).
2. **The device chain** — the app chain minus the install step, for platform surfaces that belong to
   the handset rather than to anything running on it. The notification queue is the first of these:
   it names no app, so a phone that never installed Bank still reads its queue, and it is refused
   only when the phone itself is not usable. That is also what will let a Bank notification outlive
   Bank being uninstalled. The app chain is a thin wrapper over this one, adding only the install
   check.
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

The store is the one app whose routes are not under `/apps/{its key}/`, at
`/api/phones/{phoneId}/app-store/`. It reads across the whole catalog rather than owning a noun, so
it has no prefix of its own to sit under — `/apps/store/apps` only repeated the word. It is still the
Store app, unlike the notification queue below: its guard chain runs against `AppKey.Store` like any
other app operation, so the phone must be powered on and the store installed.

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
  [Apps](#apps). The queue asks about no app, so an app the phone has not installed does not make it
  unreachable; it is refused only when the phone itself is not usable, a powered-off phone's queue
  being `409` and a suspended one `403`.
- **Publication is the publishing app's job**, on the same `IPhoneSession` as whatever caused it, so
  the notification commits atomically with the change it describes. The platform never composes one
  itself and stores no opinion about what any app's `Category` or `Payload` mean.
- **Proving the chain is app-less needs a store app, not an uninstall.** This independence used to
  be demonstrable by uninstalling Messages and reading the queue anyway. Messages is a system app
  now, so that refuses and asserts nothing, and every app with routes of its own is likewise
  permanent — so the test uses `AppKey.Bank`, which a phone has simply never installed. See
  `GuardChains_DifferOnlyByTheInstallCheck`, which is also the only thing exercising
  `PhoneAccessResult.AppNotInstalled` at all.
- **Titles carry the bare sender number, not a resolved name.** Messages deliberately does not read
  Contacts (see [Threads](#threads)) — a banner is not a reason to reverse that, so the mod resolves
  a display name itself, the same call it already makes to draw a thread.

## Calls

The dialer owns `/apps/phone/`, and the backend's half of a call is **signalling only**: who is
ringing whom, and what has become of it. There is no audio here and no concept of one — the mod
carries the sound through the game's own voice. That split is also the answer to "why does a call
need a server at all": two clients cannot agree on *answered*, or on who hung up first, and neither
can resolve a number held on another gameserver.

A call is one event-sourced stream with five states. `Ringing` and `Accepted` are the non-terminal
pair; `Rejected`, `Missed` and `Ended` are final, and the aggregate refuses every transition out of
them.

```
            ┌──────────── answer ──────────▶ Accepted ──── either hangs up ───▶ Ended
Ringing ────┼──────────── reject ──────────▶ Rejected
            ├──── caller hangs up first ──▶ Missed
            └──── window ran out ─────────▶ Missed
```

Three different things mean the same thing to the callee, so all three land on `Missed`: the ring
window ran out, the caller gave up, or the phone was off when the call was placed. **Rejected is
deliberately not one of them** — declining is a decision, and a call log that folded it into "missed"
would lose the only interesting thing about it.

Dialling reuses the send path's recipient decision, including its silences:

| Callee state | Outcome |
| --- | --- |
| Unknown number, or phone `Deactivated` | `Undeliverable`, reported to the caller |
| Phone `Suspended` | `Undeliverable`, reported to the caller |
| Caller is on the callee's blocklist | `Unanswered` — **nothing is recorded and nothing rings** |
| Phone powered off, or the dialer uninstalled | `Unanswered`, recorded as a missed call with a banner |
| Already on a call | `Busy` |
| Otherwise | `Ringing` |

Two of those are load-bearing:

- **A block is indistinguishable from nobody picking up.** Both answer `Unanswered`, for the same
  reason a blocked message reads as sent: reporting them apart would make the API a block-detector.
  The difference is invisible to the caller and total for the callee — a blocked call leaves no
  record at all, where a powered-off one leaves a missed call.
- **Nothing is queued.** A text to an unreachable phone becomes a `PendingDelivery` and arrives
  later; a call does not, because a two-hour-old ring is noise. The missed call and its banner are
  what the callee finds when they power on, and that is the dialer's whole equivalent of the queue.

**Blocking is shared, read-only.** The blocklist lives on `PhoneDevice` but belongs to Messages —
its URL and its guard chain agree about that. The dialer reads `PhoneDevice.IsBlocked` exactly as the
send path does and adds no storage and no second list, so one list is obeyed by both and edited
through one app.

**Do Not Disturb is not consulted.** It sits in `Preferences` beside the home layout and the
wallpaper, nothing server-side reads it, and it does not silence message banners either — so
silencing calls alone would be inconsistent, and would also hide from the caller that the phone rang.
Ringing is reported; whether the handset makes a noise is the mod's call.

### Expiry, without a sweep

Nothing schedules anything. A ring whose window has closed is *computed* as expired and materialised
to `Missed` the next time anything touches it — the poll, an answer, a reject, or the caller dialling
again. It is the same approach thread trimming and the notification cap already take here, and it
means a call cannot be left ringing forever by a client that simply stopped polling.

The practical consequence: `PhoneRingSeconds` plus the Bridge's poll interval *is* the ringing
experience. The window bounds how long a caller hears ringing; the poll bounds how long before the
callee hears anything at all.

## Routes

The split the URLs make visible: the phone itself, versus what runs on it.

```
Platform            POST   /api/phones
                    GET    /api/phones/{phoneId}
                    GET    /api/characters/{characterId}/phones
                    POST   /api/phones/{phoneId}/power
                    POST   /api/phones/{phoneId}/pin
                    GET    /api/phones/{phoneId}/preferences
                    PATCH  /api/phones/{phoneId}/preferences
                    GET    /api/phones/{phoneId}/apps
                    PUT    /api/phones/{phoneId}/apps/{appKey}
                    DELETE /api/phones/{phoneId}/apps/{appKey}

App store           GET    /api/phones/{phoneId}/app-store/apps

Phone app (dialer)  POST   /api/phones/{phoneId}/apps/phone/calls
                    GET    /api/phones/{phoneId}/apps/phone/calls/ringing
                    POST   /api/phones/{phoneId}/apps/phone/calls/{callId}/answer
                    POST   /api/phones/{phoneId}/apps/phone/calls/{callId}/reject
                    POST   /api/phones/{phoneId}/apps/phone/calls/{callId}/end

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

# A phone ships with its system apps installed, and powered off.
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
| Phone powered off | Queued as a `PendingDelivery` |
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

Queued messages are delivered when the number becomes reachable again, which means powering the
phone on. Safe to repeat — a still-unreachable phone simply leaves everything queued, and each
delivery leaves the queue in the same commit that appends it to the thread. Installing Messages used
to be a second such moment, back when it could be uninstalled; now that it is a system app, every
phone has it and power is the only gate.

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

The ring poll is bounded differently, and is the only phone endpoint with an HTTP rate limit:
`GET /apps/phone/calls/ringing` carries a token bucket of 100/s sustained with 300 in reserve, per
`client_id` — so per gameserver, the same partition key World's two write policies use. Every other
phone route is driven by a player doing something and is self-limiting at human speed; the ring poll
is driven by a timer, has to run at a second or two to make ringing feel instant, and is multiplied
by every connected player. That is the first traffic shape here a misconfigured client can turn into
a flood with nobody touching a phone, and `docs/bridge.md` is explicit that an unlimited endpoint is
not permission to poll it without bound. A 429 carries `Retry-After` and `retryable: true`, the
shared shape from `Program.cs`.

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
three independent surfaces instead, all delivery rather than truth:

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

**Ringing** — `GET /api/phones/{phoneId}/apps/phone/calls/ringing`, covered in [Calls](#calls). The
one latency-critical poll of the three, and the only one with a rate limit; see
[Rate limiting](#rate-limiting). It needs no ack and holds no cursor: a ring is current state rather
than a backlog, so the poll simply reports what is ringing now, and expires anything that has run
out while answering.

## Adding an app

1. One `AppKey` member and one `AppDefinition` in `AppCatalog`, deciding `IsSystemApp` — whether it
   ships with the handset and can never be removed, or is one the store offers.
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
of breaking that rule is wider than it used to be. `PhoneNotification` is the sharp edge: it is a
plain document with no stream behind it, and the repository both filters and *deletes* on the
ordinal, so a remap there changes which rows are removed rather than merely how they read.

**Nothing sits at `AppKey` 0**, so a payload that is missing the field names no app instead of
quietly naming whichever one sat there. This departs from ARCHITECTURE.md §9e's "make member 0 the
safest real default" — see the reasoning on `AppKey` itself: there is no safe answer to "which app
does this notification belong to", so an undefined value beats a plausible one. Your new member goes
at the next free number, never at 0.

`Phone` and `Store` own routes. `Settings` owns the `Preferences` document, which sits under
`Devices/` rather than behind an app prefix because a phone has preferences whether or not anything
is installed. `Messages` and `Contacts` have owned theirs from the start.

**`Bank` and `Map` are deliberately client-side, and adding routes for them here would be a
regression.** Both were scoped and rejected:

- **Bank.** Banking already transacts safely without the phone. `BankAccountAuthorization` gates
  withdraw and transfer on account ownership — the owning character for a Personal account,
  `CompanyPermissions.ManageFinances` for a Corporate one — and the Bridge already proxies ten of
  Banking's eleven routes. A phone-side Bank app would re-implement a working, *more strictly
  authorized* surface under the phone's weaker chain, which only asks "powered on, app installed".
  What it would add is the four platform checks and a `phoneId` in the audit trail; what it would
  cost is two ways to move money. If that trade is ever worth making, note that `BankAccountAuthorization`
  is `internal` and admits only `Shops.Application` today.
- **Map.** Nothing in the backend can answer "where is this character". `Character` carries no
  transform, `CharacterPresence` is a written-but-unbuilt plan designed for a ~30-60s heartbeat, and
  no `Shop`, `Bank` or `Company` has coordinates. Every blocker is in another module, and
  `npc-virtual-simulation` has an unresolved claim on shop position — so a Map app would mean
  designing a coordinate system somewhere else first.

`AppKey.Bank` and `AppKey.Map` still do real work as catalog entries: they are what the store offers
and what a handset may lack, which is also why they are the keys every guard-chain test reaches for.

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
