# Shops — Design

## Summary

Add two new modules, `Items` and `Shops`, following the existing modulith
shape (`Domain`/`Application`/`Infrastructure`/`Api` per module, composed by
`src/Api`). `Items` is a small staff-curated catalog of purchasable game
items (display name + prefab class name). `Shops` lets a `Character` or
`Company` open a shop, list catalog items for sale at a price with finite
stock, and lets other characters purchase a listing — settled by dispatching
a cross-module `TransferCommand` into `Banking.Application`, moving money
from the buyer's bank account to the shop's payout account. Shops is also
the first module to add a SignalR hub, pushing live listing/stock changes to
subscribers.

This revives the intent of the old `89-shops` branch (`Shop`/`ShopListing`/
`Item`/`Prefab`, owned by a `Character` or `Company`), which never grew past
a single-commit domain skeleton — no pricing, no purchase flow, no
Application/Api layer (see `MIGRATION.md`'s legacy-app analysis: *"Countries,
Items/Prefab, Shops — Domain-only... unused/future"*). None of that code is
reused directly; only the vocabulary and the ownership shape carry over.

## Existing-code changes required first

- **Move `BankAccountId` from `Banking.Domain` to `Shared.Kernel`.** Today
  it's the one aggregate id referenced only within `Banking`; no other
  module stores a `BankAccountId`. `Shop.PayoutBankAccountId` is the first
  cross-module reference to it, so per the established rule ("cross-module
  ID references go through `Shared.Kernel`... never let Domain make the
  [validating] query itself", `ARCHITECTURE.md` §9e) it needs to move,
  exactly like `CharacterId`/`CompanyId`/`AccountId` already did for their
  own first cross-module consumer. Mechanical change: relocate the
  `[StronglyTypedId] public partial struct BankAccountId;` file, add the
  same "Owned by the Banking module; lives here so other modules can
  reference it without depending on Banking.Domain" doc comment, fix
  `using`s in `Banking.Domain`/`Application`/`Infrastructure`/`Api`. No
  behavior change.
- **Add `ItemId` to `Shared.Kernel`** (owned by the new `Items` module),
  same pattern, needed from the start since `Shops` references it.

## New module: `Items`

### Domain (`Items.Domain`)

- `Item` — `Id (ItemId), DisplayName (string), PrefabClassName (string)`.
  Plain aggregate, no state transitions beyond creation — no
  rename/deactivate methods (not requested, YAGNI).
- Event: `ItemCreated(ItemId Id, string DisplayName, string PrefabClassName)`.
- `Item.Create(ItemCreated @event)` static factory, `Apply` per the Marten
  convention (declared on `ItemProjection` in `Items.Infrastructure`, not on
  the aggregate itself — gotcha 1 in `ARCHITECTURE.md` §9e).

### Application (`Items.Application`)

- `CreateItemCommand(string DisplayName, string PrefabClassName) : IRequest<CreateItemResult>`
  `union CreateItemResult(Created(ItemId ItemId))` — no failure branch; no
  uniqueness constraint requested for v1.
- `ItemsQuery : IRequest<IReadOnlyList<Item>>` — full catalog listing.
- `ItemLookupQuery(ItemId ItemId) : IRequest<ItemLookupResult>`
  `union ItemLookupResult(Found(Item Item), NotFound)` — doubles as both the
  `GET /api/items/{id}` backing query and the cross-module contract `Shops`
  dispatches into to validate an `ItemId`, same as `CharacterLookupQuery`
  serves both roles today.
- `AssemblyMarker` (empty, for the host's centralized `AddMediator` call).

### Infrastructure (`Items.Infrastructure`)

- Secondary named Marten store `IItemsStore`, schema `items` (every module
  after `Accounts` uses this pattern).
- `ItemProjection : SingleStreamProjection<Item, ItemId>`.
- `IItemRepository` / `MartenItemRepository` — `IAsyncDisposable` only (owns
  its own session, gotcha 6).

### Api (`Items.Api`)

- `ItemModule.AddItemModule(services, configuration)` /
  `MapItemModule(app)`, same static-extension-pair shape as every module.
- Scope: `gameserver:items:manage` (create only — read endpoints are
  `.RequireAuthorization()` with no extra scope, matching Banking's
  manage-vs-plain-auth split).
- DTOs: `CreateItemRequestDto { string DisplayName; string PrefabClassName; } → ToCommand()`,
  `ItemDto { Guid ItemId; string DisplayName; string PrefabClassName; } → Create(Item source)`.
- Endpoints: `POST api/items` (manage scope) → `Created` → 200 `ItemDto`;
  `GET api/items` → `List<ItemDto>`; `GET api/items/{itemId:guid}` →
  `Found` → 200 `ItemDto`, `NotFound` → 404.

## New module: `Shops`

### Domain (`Shops.Domain`)

Two aggregates on separate Marten streams — mirroring the existing
`Bank`/`BankAccount` split (a container vs. the thing that mutates under
concurrent write pressure), rather than the old branch's single
`Shop.Listings` collection. This means concurrent purchases against
*different* listings in the same shop never serialize against each other.

**`Shop`**
- `Id (ShopId), OwnerType (ShopOwnerType: Personal|Corporate), OwnerCharacterId (CharacterId?), OwnerCompanyId (CompanyId?), DisplayName (string), PayoutBankAccountId (BankAccountId)`.
- Event: `ShopOpened(ShopId Id, ShopOwnerType OwnerType, CharacterId? OwnerCharacterId, CompanyId? OwnerCompanyId, string DisplayName, BankAccountId PayoutBankAccountId)`.
- `Shop.Create(ShopOpened @event)` — no further mutation methods for v1
  (no rename/close — not requested).
- `ShopId`, `ShopOwnerType` are module-local (`Shops.Domain`), not
  `Shared.Kernel` — nothing outside `Shops` needs to reference a shop,
  matching how `CompanyApplicationId` stayed module-local.

**`ShopListing`**
- `Id (ShopListingId), ShopId (ShopId), ItemId (ItemId), Price (decimal), Stock (int), IsActive (bool)`.
- Events: `ListingCreated(ShopListingId Id, ShopId ShopId, ItemId ItemId, decimal Price, int Stock)`,
  `ListingUpdated(ShopListingId Id, decimal Price, int Stock)`,
  `ListingPurchased(ShopListingId Id, int Quantity, int NewStock)`,
  `ListingRemoved(ShopListingId Id)` (soft delete — sets `IsActive = false`;
  Marten streams aren't deleted).
- Methods (mutate + return event, matching `Company`'s style):
  - `ShopListing.Create(ListingCreated @event)`
  - `UpdatePriceAndStock(decimal price, int stock)` — throws
    `ArgumentOutOfRangeException` for negative price/stock (programming
    error, left to propagate as 500 per the domain-guard convention:
    `ARCHITECTURE.md` §9e, "reserve [catch-in-handler] for exceptions
    representing a business rule violation... not programming errors").
  - `Purchase(int quantity)` — throws `InsufficientStockException`
    (`Shops.Domain/Exceptions`) if `quantity > Stock`, else returns
    `ListingPurchased` with the decremented stock.
  - `Remove()` — throws `ListingAlreadyRemovedException` if `!IsActive`.

### Application (`Shops.Application`)

Commands/queries, each following the existing `union`-result + handler
convention:

- `OpenShopCommand(ShopOwnerType OwnerType, CharacterId? OwnerCharacterId, CompanyId? OwnerCompanyId, string DisplayName, BankAccountId PayoutBankAccountId) : IRequest<OpenShopResult>`
  `union OpenShopResult(Opened(ShopId ShopId), CharacterNotFound, CompanyNotFound)`.
  No `ActingCharacterId`/authorization check — opening a bank account has
  none either (`OpenBankAccountCommand`/`OpenCorporateBankAccountCommand`
  take no acting-identity parameter at all), so opening a shop follows the
  same precedent. Handler: `CharacterLookupQuery` or `CompanyLookupQuery`
  (both already exist, used the same way `OpenCorporateBankAccountCommand`
  already uses `CompanyLookupQuery`) depending on `OwnerType` (404 variant
  if missing) → create.
- `ShopsQuery : IRequest<IReadOnlyList<Shop>>`.
- `ShopQuery(ShopId ShopId) : IRequest<ShopQueryResult>`
  `union ShopQueryResult(Found(Shop Shop, IReadOnlyList<ShopListing> Listings), NotFound)`
  (listings filtered to `IsActive`).
- `AddListingCommand(ShopId ShopId, ItemId ItemId, decimal Price, int Stock, CharacterId ActingCharacterId) : IRequest<AddListingResult>`
  `union AddListingResult(Added(ShopListingId ListingId), ShopNotFound, ItemNotFound, NotAuthorized)`.
- `UpdateListingCommand(ShopId ShopId, ShopListingId ListingId, decimal Price, int Stock, CharacterId ActingCharacterId) : IRequest<UpdateListingResult>`
  `union UpdateListingResult(Updated, ShopNotFound, ListingNotFound, NotAuthorized)`.
- `RemoveListingCommand(ShopId ShopId, ShopListingId ListingId, CharacterId ActingCharacterId) : IRequest<RemoveListingResult>`
  `union RemoveListingResult(Removed, ShopNotFound, ListingNotFound, NotAuthorized)`.
- `PurchaseListingCommand(ShopId ShopId, ShopListingId ListingId, int Quantity, CharacterId BuyerCharacterId, BankAccountId BuyerBankAccountId) : IRequest<PurchaseListingResult>`
  `union PurchaseListingResult(Purchased(decimal TotalPaid, int NewStock), ShopNotFound, ListingNotFound, InsufficientStock, ListingChangedConcurrently, BuyerAccountNotFound, NotAuthorized, InsufficientBalance)`
  (`BuyerAccountNotFound`/`NotAuthorized`/`InsufficientBalance` names match
  `TransferResult`'s own case names 1:1, since they're mapped straight
  through. `ListingChangedConcurrently` replaces an earlier draft's
  `RefundedAfterStockConflict` — see the Purchase saga section below for why.)

**Authorization helper** (`Shops.Application/Common/ShopAuthorization.cs`),
mirroring `Banking.Application.Common.BankAccountAuthorization` exactly:

```csharp
internal static class ShopAuthorization
{
    public static async ValueTask<bool> CanManageAsync(
        Shop shop, CharacterId actingCharacterId, IMediator mediator, CancellationToken cancellationToken)
    {
        if (shop.OwnerType == ShopOwnerType.Personal)
        {
            return shop.OwnerCharacterId == actingCharacterId;
        }

        var permissionsLookup = await mediator.Send(
            new CompanyMemberPermissionsQuery(shop.OwnerCompanyId!.Value, actingCharacterId), cancellationToken);

        return permissionsLookup is CompanyMemberPermissionsResult.Found found
            && found.Permissions.HasFlag(CompanyPermissions.ManageShops);
    }
}
```

Used by `AddListingHandler`/`UpdateListingHandler`/`RemoveListingHandler`
(not `OpenShopHandler` — see above; not `PurchaseListingHandler` — the buyer
needs no shop-management permission, only a valid, character-authorized
source account, which `Banking`'s own `TransferHandler` already enforces on
the source side).

**Existing-module change:** add `CompanyPermissions.ManageShops = ManageFinances << 1`
in `Companies.Domain/CompanyPermissions.cs`.

**Purchase saga** (`PurchaseListingHandler`) — ordered specifically because
a `Shops` write and a `Banking` write can't share one Postgres transaction
(separate Marten schemas; `ARCHITECTURE.md` §9e reserves exactly this case
for "the saga/process-manager approach... not this [same-module] shortcut"):

**Corrected during planning:** an earlier draft of this saga charged the
buyer *before* decrementing stock, with a compensating reverse `TransferCommand`
refunding them if the decrement then failed. That reverse transfer is
unauthorizable: `TransferCommand`'s source-account check requires the acting
character to own (or hold a permission on) the *source* account, and the
buyer is never authorized on the *shop's own* payout account — so a refund
transfer sourced from it would itself fail `NotAuthorized`, even though
nothing went wrong. The fix is to reserve stock first and charge second:

1. Load the shop (404 → `ShopNotFound`) and the listing (404 →
   `ListingNotFound` if missing or it belongs to a different shop).
2. Reserve the stock: `listingRepository.PurchaseAsync(listingId, quantity)`
   — a single repository operation that loads the listing, calls
   `listing.Purchase(quantity)`, and appends+saves, using Marten's
   `FetchForWriting`-based optimistic concurrency so two concurrent
   purchases against the same listing can never both succeed (see
   Infrastructure, below). `InsufficientStockException` → `InsufficientStock`.
   A concurrency conflict (another purchase committed against this exact
   listing first) → `ListingChangedConcurrently` — no money has moved yet
   either way, so there's nothing to compensate.
3. Only now, with stock durably reserved, charge payment:
   `mediator.Send(new TransferCommand(request.BuyerBankAccountId, shop.PayoutBankAccountId, request.BuyerCharacterId, listing.Price * request.Quantity))`
   into `Banking.Application`. On `Transferred` → return
   `Purchased(totalPaid, listing.Stock)`.
4. If the transfer instead fails (`BankAccountNotFound` →
   `BuyerAccountNotFound`, `NotAuthorized` → `NotAuthorized`,
   `InsufficientBalance` → `InsufficientBalance`), restore the reserved
   stock via `listing.RestoreStock(quantity)` (append + save) and return the
   mapped failure. This is an **in-module** compensating action — no
   cross-module authorization involved, unlike the rejected reverse-transfer
   approach — which is exactly why reserving stock before charging, rather
   than the other order, closes the problem instead of needing a workaround
   for it. `TransferResult.TargetBankAccountNotFound` (the shop's own payout
   account disappearing) is not a caller-triggerable business case, so it
   propagates as a 500 rather than adding a dedicated result branch.

### Infrastructure (`Shops.Infrastructure`)

- Secondary named Marten store `IShopsStore`, schema `shops`, registering
  **both** `Shop` and `ShopListing` as separate `SingleStreamProjection`s
  against the same store — the exact pattern already verified for
  `Bank`/`BankAccount` in `Banking`.
- `IShopRepository`/`MartenShopRepository`,
  `IShopListingRepository`/`MartenShopListingRepository`, each
  `IAsyncDisposable` only.
- `IShopListingRepository` additionally exposes `PurchaseAsync(ShopListingId, int, CancellationToken) : ValueTask<ShopListing>`,
  implemented via Marten's `FetchForWriting`/`AppendOne` — the first use of
  Marten's optimistic-concurrency stream API in this codebase (every other
  repository just calls a plain `Append`). Necessary because two concurrent
  purchases against the same listing must never both succeed; see the
  Purchase saga section above.

### Api (`Shops.Api`)

DTOs (`Shops.Api/Shops`):
- `OpenShopRequestDto { string OwnerType; Guid? OwnerCharacterId; Guid? OwnerCompanyId; string DisplayName; Guid PayoutBankAccountId; } → ToCommand()` —
  manually validates exactly one of `OwnerCharacterId`/`OwnerCompanyId` is
  set, matching `OwnerType`, returning `Results.Problem(400)` on violation
  (same manual-validation style as `BankingEndpoints`'s
  exactly-one-of-`characterId`/`companyId` check — no attribute-based
  validation pipeline exists in this codebase).
- `ShopDto { Guid ShopId; string OwnerType; Guid? OwnerCharacterId; Guid? OwnerCompanyId; string DisplayName; Guid PayoutBankAccountId; } → Create(Shop source)`.
- `ShopDetailsDto` — `ShopDto` plus `IReadOnlyList<ShopListingDto> Listings`.
- `ShopListingDto { Guid ListingId; Guid ItemId; decimal Price; int Stock; } → Create(ShopListing source)`.
- `AddListingRequestDto { Guid ItemId; decimal Price; int Stock; Guid ActingCharacterId; } → ToCommand(Guid shopId)`.
- `UpdateListingRequestDto { decimal Price; int Stock; Guid ActingCharacterId; } → ToCommand(Guid shopId, Guid listingId)`.
- `PurchaseListingRequestDto { int Quantity; Guid BuyerCharacterId; Guid BuyerBankAccountId; } → ToCommand(Guid shopId, Guid listingId)`.

Scopes: `gameserver:shops:manage` (open shop), `gameserver:shops:write`
(add/update/remove listing, purchase) — same manage-vs-write split as
Banking. Plain reads (`GET`) are `.RequireAuthorization()` only.

Endpoints (`ShopsEndpoints.cs`):
- `POST api/shops` → `Opened` → 200 `ShopDto`; `CharacterNotFound`/`CompanyNotFound` → 404.
- `GET api/shops` → `List<ShopDto>`.
- `GET api/shops/{shopId:guid}` → `Found` → 200 `ShopDetailsDto`; `NotFound` → 404.
- `POST api/shops/{shopId:guid}/listings` → `Added` → 200 `ShopListingDto`;
  `ShopNotFound`/`ItemNotFound` → 404; `NotAuthorized` → 403.
- `PUT api/shops/{shopId:guid}/listings/{listingId:guid}` → `Updated` → 200;
  `ShopNotFound`/`ListingNotFound` → 404; `NotAuthorized` → 403.
- `DELETE api/shops/{shopId:guid}/listings/{listingId:guid}?actingCharacterId={guid}`
  (query param — no body on `DELETE`, same reasoning as the
  acting-identity-on-`GET` precedent from company applications) → `Removed`
  → 204; `ShopNotFound`/`ListingNotFound` → 404; `NotAuthorized` → 403.
- `POST api/shops/{shopId:guid}/listings/{listingId:guid}/purchase` →
  `Purchased` → 200 `{ decimal TotalPaid; int NewStock; }`;
  `ShopNotFound`/`ListingNotFound`/`BuyerAccountNotFound` → 404;
  `InsufficientStock`/`InsufficientBalance`/`ListingChangedConcurrently` →
  409; `NotAuthorized` → 403.

### Real-time (`ShopsHub`)

First SignalR hub in the codebase — `ARCHITECTURE.md` §5.3 sketches a
generic "Marten subscription feeds a SignalR Hub" pipeline for the
Bridge/NPC raw event-batch case; this is a narrower, simpler mechanism
purpose-built for live listing updates, not that pipeline:

- `Shops.Api/ShopsHub.cs`: `public sealed class ShopsHub : Hub` with
  `SubscribeToShop(Guid shopId)` / `UnsubscribeFromShop(Guid shopId)`
  client methods, joining/leaving a per-shop SignalR group
  (`$"shop-{shopId}"`).
- `AddShopModule(services, configuration)` calls `services.AddSignalR()`
  (safe to call from multiple modules if a future one also needs it — its
  registrations are idempotent `TryAdd`-style). `MapShopModule(app)` calls
  `app.MapHub<ShopsHub>("hubs/shops").RequireAuthorization()` alongside the
  REST endpoint group.
- **Push happens from `Shops.Api`, not `Shops.Application`.** A Mediator
  handler can't depend on `IHubContext<ShopsHub>` — `*.Application`
  projects reference only their own `*.Domain` (`ARCHITECTURE.md` §9e), and
  SignalR types live in the Api layer alongside the hub itself. So each
  endpoint pushes *after* its mediator call succeeds, via a small
  `ShopsHubNotifier` helper (`Shops.Api/ShopsHubNotifier.cs`) wrapping
  `IHubContext<ShopsHub>`, injected into the Minimal API delegates like any
  other DI service:
  - `AddListing`/`UpdateListing`/purchase success → `NotifyListingChangedAsync(shopId, listingId, price, stock)` → group receives `"ListingChanged"`.
  - Remove success → `NotifyListingRemovedAsync(shopId, listingId)` → group receives `"ListingRemoved"`.
- No catch-up/replay protocol for v1 — per §5.3, "SignalR is a delivery
  convenience, not the source of truth"; a client that missed messages
  (disconnect, restart) just re-fetches `GET api/shops/{shopId}` to resync.

**Host change required** (`src/Api/Program.cs`): browsers can't set a
custom `Authorization` header on a WebSocket upgrade request, so the
standard SignalR-over-JWT pattern reads the token from the query string on
hub paths. Add to the existing (shared, host-level) `JwtBearerEvents`:

```csharp
OnMessageReceived = context =>
{
    var accessToken = context.Request.Query["access_token"];
    if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
    {
        context.Token = accessToken;
    }
    return Task.CompletedTask;
},
```

This is authentication plumbing shared by any future hub, not Shops
business logic, so it belongs in the host's existing central config next to
the token-revocation `OnTokenValidated` check already there — not
duplicated per-module.

## Keycloak

Add three client scopes to `infra/keycloak/eliferpg-realm.json`
(`gameserver:items:manage`, `gameserver:shops:manage`,
`gameserver:shops:write`), granted to `gameserver-dev`, mirroring the
existing `gameserver:banking:*` blocks exactly (both the scope list entry
and the `clientScopes` definition).

## Host wiring (`src/Api/Program.cs`)

- Add `typeof(ELifeRPG.Items.Application.AssemblyMarker)` and
  `typeof(ELifeRPG.Shops.Application.AssemblyMarker)` to the central
  `AddMediator` assembly list.
- Add `builder.Services.AddItemModule(builder.Configuration).AddShopModule(builder.Configuration)`.
- Add `app.MapItemModule(); app.MapShopModule();`.
- Add the `OnMessageReceived` query-string-token handler above.

## Docs

`docs/items.md` and `docs/shops.md` (curl walkthroughs, same format as
`docs/banking.md`/`docs/companies.md`), plus a `README.md` status-line
update and a new `MIGRATION.md` section, following the existing
per-feature format — content deferred to implementation, not drafted here.

## Suggested build order

1. `Shared.Kernel`: move `BankAccountId`, add `ItemId`.
2. `Items` module end-to-end (Domain → Application → Infrastructure → Api),
   Keycloak scope, `docs/items.md`.
3. `Shops` module minus purchase and SignalR: `Shop`/`ShopListing` domain,
   `OpenShop`/`ShopsQuery`/`ShopQuery`/Add/Update/RemoveListing,
   `CompanyPermissions.ManageShops`, Infrastructure, Api (REST only).
4. Purchase flow (cross-module `Banking` settlement, stock reserved before
   payment, in-module restore on payment failure) — the highest-risk piece,
   gets its own integration test pass, plus a dedicated concurrency-race
   verification pass for `IShopListingRepository.PurchaseAsync`'s
   `FetchForWriting` usage (Marten's exact concurrency-conflict exception
   type isn't used anywhere else in this codebase yet, so it's confirmed
   empirically rather than assumed).
5. `ShopsHub` + host JWT query-token wiring + `ShopsHubNotifier` push
   wiring in the REST endpoints.
6. Docs/README/MIGRATION.md, remaining Keycloak scopes if not done in step 3.

## Testing

- `Items.Domain.UnitTests`: `Item.Create`/`Apply` replay.
- `Items.IntegrationTests`: `CreateItemCommand` → `ItemsQuery`/`ItemLookupQuery` round-trip.
- `Shops.Domain.UnitTests`: `Shop.Create`; `ShopListing.Create`/`UpdatePriceAndStock`/`Purchase`
  (including `InsufficientStockException`) /`Remove` (including
  `ListingAlreadyRemovedException`); `Apply`-replay tests for both
  aggregates.
- `Shops.IntegrationTests`: needs `Accounts`, `Characters`, `Companies`,
  `Banking`, and `Items` infrastructure wired up (same escalating
  cross-module dependency pattern `Banking.IntegrationTests` already has).
  Covers: open Personal/Corporate shop (character/company not-found
  branches) → add/update/remove listing (owner-authorized and
  permission-denied branches for both ownership types) → purchase
  happy-path (stock decrements, money moves, `ShopDto`/balance both
  verified against live Postgres) → `InsufficientStock`/`InsufficientBalance`
  branches (the latter asserting stock was restored, not left decremented)
  → a genuine two-concurrent-requests race test against a listing with
  stock 1, asserting exactly one buyer is charged and stock never goes
  negative.
- No automated test for the SignalR push itself planned beyond a manual
  verification pass (matching how this codebase verifies other
  infrastructure-adjacent pieces, e.g. OpenTelemetry reaching Tempo) —
  revisit if a `Microsoft.AspNetCore.SignalR.Client`-based integration test
  turns out cheap once the hub exists.

## Out of scope

- Renaming/closing a shop, renaming/updating an `Item`'s catalog entry.
- Reserving/holding stock during checkout (a purchase either fully
  succeeds or fully fails against the stock read at handler time).
- Any in-game item-granting mechanics (spawning the physical item for the
  buyer) — this backend only settles payment and decrements a catalog
  count; wiring an actual grant is a Bridge/gameserver-mod concern for a
  later iteration, same boundary every other module already keeps (Banking
  doesn't simulate physical cash either).
- A Marten-subscription-fed hub (the fuller §5.3 pipeline) — direct push
  from the Api layer is the deliberately simpler v1 mechanism.
- Rate-limiting/throttling purchases.
- An admin/staff UI for managing the `Items` catalog — `POST api/items` is
  the only interface for v1.
