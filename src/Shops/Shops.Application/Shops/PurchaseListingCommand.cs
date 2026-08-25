using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain.Exceptions;

namespace ELifeRPG.Shops.Application.Shops;

public union PurchaseListingResult(
    PurchaseListingResult.Purchased,
    PurchaseListingResult.ShopNotFound,
    PurchaseListingResult.ListingNotFound,
    PurchaseListingResult.InsufficientStock,
    PurchaseListingResult.ListingChangedConcurrently,
    PurchaseListingResult.BuyerAccountNotFound,
    PurchaseListingResult.NotAuthorized,
    PurchaseListingResult.InsufficientBalance)
{
    public record Purchased(decimal TotalPaid, int NewStock);

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
    IShopListingRepositoryFactory listingRepositoryFactory,
    IBankAccountRepositoryFactory bankAccountRepositoryFactory,
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

        await using var transaction = await transactionFactory.BeginAsync(cancellationToken);

        // Repositories obtained from a cross-module transaction handle are intentionally never
        // disposed here — only `transaction` owns the underlying connection/transaction.
        var crossModuleListingRepository = listingRepositoryFactory.CreateFor(transaction.Handle);

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

        var bankAccountRepository = bankAccountRepositoryFactory.CreateFor(transaction.Handle);

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

        try
        {
            await crossModuleListingRepository.SaveChangesAsync(cancellationToken);
        }
        catch (ListingPurchaseConflictException)
        {
            return new PurchaseListingResult.ListingChangedConcurrently();
        }

        await bankAccountRepository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PurchaseListingResult.Purchased(totalPrice, listing.Stock);
    }
}
