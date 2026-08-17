using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Common;
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
        var buyerAccount = await bankAccountRepository.FindByIdAsync(request.BuyerBankAccountId, cancellationToken);
        if (buyerAccount is null)
        {
            return new PurchaseListingResult.BuyerAccountNotFound();
        }

        var payoutAccount = await bankAccountRepository.FindByIdAsync(shop.PayoutBankAccountId, cancellationToken);
        if (payoutAccount is null)
        {
            // The shop's own payout account disappearing isn't a caller-triggerable business case —
            // propagates as a 500 rather than adding a dedicated result branch. Same deliberate gap
            // TransferHandler's TargetBankAccountNotFound case left unmapped before this migration —
            // see ARCHITECTURE.md §9e.
            throw new InvalidOperationException($"Shop {request.ShopId}'s payout bank account {shop.PayoutBankAccountId} does not exist.");
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
