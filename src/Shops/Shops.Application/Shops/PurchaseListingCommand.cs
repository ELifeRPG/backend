using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain.Exceptions;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;

namespace ELifeRPG.Shops.Application.Shops;

public union PurchaseListingResult(
    PurchaseListingResult.Purchased,
    PurchaseListingResult.ShopNotFound,
    PurchaseListingResult.ListingNotFound,
    PurchaseListingResult.InsufficientStock,
    PurchaseListingResult.ListingChangedConcurrently,
    PurchaseListingResult.BuyerAccountNotFound,
    PurchaseListingResult.NotAuthorized,
    PurchaseListingResult.InsufficientBalance,
    PurchaseListingResult.GrantTooLarge,
    PurchaseListingResult.ItemNotInCatalog)
{
    public record Purchased(decimal TotalPaid, int NewStock, IReadOnlyList<GrantedInstance> GrantedInstances);

    public record ShopNotFound;

    public record ListingNotFound;

    public record InsufficientStock;

    /// <summary>
    /// Originally: another purchase committed against this exact listing between this request's
    /// read and write. As of the migration onto ICrossModuleTransaction, the purchase flow acquires
    /// a Postgres row lock before reading/mutating the listing (see
    /// IShopListingRepository.ReserveStockAsync), which prevents this exact race from being
    /// observable this way — a second concurrent purchaser instead deterministically gets
    /// InsufficientStock once the first purchase commits and releases the lock. This case can no
    /// longer actually occur via the current row-lock-based purchase flow; it's retained purely for
    /// interface/API stability (Shops.Api still maps this union case).
    /// </summary>
    public record ListingChangedConcurrently;

    public record BuyerAccountNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;

    /// <summary>
    /// The purchased quantity would mint more item instances than
    /// <c>WorldSettings.MaxInstancesPerGrant</c> allows. Evaluated at the precheck, before
    /// <c>transactionFactory.BeginAsync</c> — no payment is ever taken for an order that cannot be
    /// fulfilled.
    /// </summary>
    public record GrantTooLarge(int Requested, int MaxInstancesPerGrant);

    /// <summary>
    /// The listing's <c>ItemId</c> no longer resolves to a catalog entry. A listing's <c>ItemId</c> is
    /// only validated when the listing is created (see <c>AddListingHandler</c>) — nothing guarantees
    /// the catalog entry still exists later. Normally returned from the precheck (a batched
    /// <c>ItemCatalogEntriesQuery</c> dispatch, before <c>transactionFactory.BeginAsync</c>), same as
    /// <see cref="GrantTooLarge"/> — no payment is ever taken for an order that cannot be fulfilled.
    /// Can still, in principle, be produced from a caught <see cref="ItemNotInCatalogException"/> if
    /// the in-transaction grant's defense-in-depth check ever fires (see
    /// <c>IItemInstanceRepository.GrantAsync</c>'s prefab-taking overload) — in that case both bank
    /// legs have already been appended, but neither leg nor the grant has been saved, and the
    /// transaction is never committed, so disposing the uncommitted
    /// <see cref="ICrossModuleTransaction"/> still rolls back and no payment moves.
    /// </summary>
    public record ItemNotInCatalog(ItemId ItemId);
}

public sealed record PurchaseListingCommand(
    ShopId ShopId,
    ShopListingId ListingId,
    int Quantity,
    CharacterId BuyerCharacterId,
    BankAccountId BuyerBankAccountId) : IRequest<PurchaseListingResult>;

public sealed class PurchaseListingHandler(
    IShopRepository shopRepository,
    IShopListingRepository listingRepository,
    ICrossModuleTransactionFactory transactionFactory,
    IMediator mediator)
    : IRequestHandler<PurchaseListingCommand, PurchaseListingResult>
{
    public async ValueTask<PurchaseListingResult> Handle(PurchaseListingCommand request, CancellationToken cancellationToken)
    {
        var shop = await shopRepository.FindByIdAsync(request.ShopId, cancellationToken);
        if (shop is null)
        {
            return new PurchaseListingResult.ShopNotFound();
        }

        // Plain, non-transactional precheck — same TOCTOU-tolerant behavior as before this migration
        // (a listing removed between this check and ReserveStockAsync below is not newly possible;
        // it was already possible between this same check and the old PurchaseAsync call).
        var precheck = await listingRepository.FindByIdAsync(request.ListingId, cancellationToken);
        if (precheck is null || precheck.ShopId != request.ShopId || !precheck.IsActive)
        {
            return new PurchaseListingResult.ListingNotFound();
        }

        // Catalog-resolution precheck — dispatches Items.Application's public, batched
        // ItemCatalogEntriesQuery via IMediator (the same public-contract exception §9e sanctions
        // everywhere else in this handler), not World's own IItemCatalogResolver. Resolving here,
        // before any transaction opens and before any lock is taken, means the in-transaction grant
        // below can receive an already-resolved prefab and do pure inserts with no external dispatch
        // while it holds the listing lock and both bank-account locks (see the review that added this
        // precheck — a second pooled connection opened mid-transaction is a resource-starvation risk
        // under pool saturation, even though it adds no lock-ordering edge).
        var catalogEntries = await mediator.Send(new ItemCatalogEntriesQuery([precheck.ItemId]), cancellationToken);
        if (!catalogEntries.TryGetValue(precheck.ItemId, out var catalogEntry))
        {
            return new PurchaseListingResult.ItemNotInCatalog(precheck.ItemId);
        }

        var prefabClassName = catalogEntry.PrefabClassName;

        // Grant-size precheck — dispatches World.Application's own public WorldSettingsQuery via
        // IMediator, the sanctioned Application->Application borrow (a plain scoped repository
        // injection is not). Must happen before transactionFactory.BeginAsync: never take payment for
        // an order that cannot be fulfilled.
        var worldSettings = await mediator.Send(new WorldSettingsQuery(), cancellationToken);
        if (request.Quantity > worldSettings.MaxInstancesPerGrant)
        {
            return new PurchaseListingResult.GrantTooLarge(request.Quantity, worldSettings.MaxInstancesPerGrant);
        }

        await using var transaction = await transactionFactory.BeginAsync(cancellationToken);

        // Repositories enlisted in a cross-module transaction are intentionally never disposed here —
        // only `transaction` owns the underlying connection/transaction.
        var crossModuleListingRepository = transaction.Enlist<IShopListingRepository>();

        ShopListing listing;
        try
        {
            listing = await crossModuleListingRepository.ReserveStockAsync(request.ListingId, request.Quantity, cancellationToken);
        }
        catch (InsufficientStockException)
        {
            return new PurchaseListingResult.InsufficientStock();
        }

        var totalPrice = listing.Price * request.Quantity;

        var bankAccountRepository = transaction.Enlist<IBankAccountRepository>();

        // Lock acquisition order must not depend on buyer/payout role: which account is "buyer" and
        // which is "payout" varies per request, so locking in request order (buyer, then payout) can
        // have two concurrent purchases — each buying from the other's shop — take the same two
        // account locks in opposite orders, deadlocking in Postgres (SQLSTATE 40P01). Sorting by the
        // underlying id before locking makes the acquisition order the same for both transactions
        // regardless of role, which rules that out.
        BankAccount buyerAccount;
        BankAccount payoutAccount;
        if (request.BuyerBankAccountId == shop.PayoutBankAccountId)
        {
            // Buying from one's own shop — buyer and payout are the same account. Lock it once.
            var account = await bankAccountRepository.FetchForUpdateAsync(request.BuyerBankAccountId, cancellationToken);
            if (account is null)
            {
                return new PurchaseListingResult.BuyerAccountNotFound();
            }

            buyerAccount = account;
            payoutAccount = account;
        }
        else
        {
            var (firstId, secondId) = request.BuyerBankAccountId.Value.CompareTo(shop.PayoutBankAccountId.Value) < 0
                ? (request.BuyerBankAccountId, shop.PayoutBankAccountId)
                : (shop.PayoutBankAccountId, request.BuyerBankAccountId);

            var firstAccount = await bankAccountRepository.FetchForUpdateAsync(firstId, cancellationToken);
            var secondAccount = await bankAccountRepository.FetchForUpdateAsync(secondId, cancellationToken);

            var fetchedBuyerAccount = firstId == request.BuyerBankAccountId ? firstAccount : secondAccount;
            var fetchedPayoutAccount = firstId == shop.PayoutBankAccountId ? firstAccount : secondAccount;

            if (fetchedBuyerAccount is null)
            {
                return new PurchaseListingResult.BuyerAccountNotFound();
            }

            if (fetchedPayoutAccount is null)
            {
                // The shop's own payout account disappearing isn't a caller-triggerable business case —
                // propagates as a 500 rather than adding a dedicated result branch. Same deliberate gap
                // TransferHandler's TargetBankAccountNotFound case left unmapped before this migration —
                // see ARCHITECTURE.md §9e.
                throw new InvalidOperationException($"Shop {request.ShopId}'s payout bank account {shop.PayoutBankAccountId} does not exist.");
            }

            buyerAccount = fetchedBuyerAccount;
            payoutAccount = fetchedPayoutAccount;
        }

        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(buyerAccount, request.BuyerCharacterId, mediator, cancellationToken);

        BankAccountTransferredOut outEvent;
        try
        {
            outEvent = buyerAccount.TransferOut(request.BuyerCharacterId, isAuthorized, shop.PayoutBankAccountId, totalPrice);
        }
        catch (BankAccountAuthorizationException)
        {
            return new PurchaseListingResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new PurchaseListingResult.InsufficientBalance();
        }

        var inEvent = payoutAccount.ReceiveTransfer(request.BuyerBankAccountId, totalPrice);

        bankAccountRepository.Append(request.BuyerBankAccountId, outEvent);
        bankAccountRepository.Append(shop.PayoutBankAccountId, inEvent);

        // Repositories enlisted in a cross-module transaction are intentionally never disposed here —
        // only `transaction` owns the underlying connection/transaction.
        var itemInstanceRepository = transaction.Enlist<IItemInstanceRepository>();

        IReadOnlyList<GrantedInstance> grantedInstances;
        try
        {
            // The prefab-taking overload: it does pure in-memory inserts with no external dispatch,
            // since prefabClassName was already resolved at the precheck above, before any lock was
            // taken. GrantAsync itself still takes no row lock and adds no new edge to the sorted
            // bank-account lock ordering above. The catch below is defense in depth, not the normal
            // path — the precheck already turned a missing catalog entry into a pre-payment rejection.
            grantedInstances = await itemInstanceRepository.GrantAsync(
                listing.ItemId,
                prefabClassName,
                request.Quantity,
                request.BuyerCharacterId,
                ItemOrigin.ShopPurchase,
                new OriginRef("Shops", request.ListingId.Value.ToString()),
                cancellationToken);
        }
        catch (ItemNotInCatalogException)
        {
            return new PurchaseListingResult.ItemNotInCatalog(listing.ItemId);
        }

        try
        {
            await crossModuleListingRepository.SaveChangesAsync(cancellationToken);
        }
        catch (ListingPurchaseConflictException)
        {
            return new PurchaseListingResult.ListingChangedConcurrently();
        }

        await bankAccountRepository.SaveChangesAsync(cancellationToken);
        await itemInstanceRepository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PurchaseListingResult.Purchased(totalPrice, listing.Stock, grantedInstances);
    }
}
