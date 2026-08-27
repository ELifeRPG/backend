using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Application.Shops;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Items;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class PurchaseListingTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Purchase_WithSufficientStockAndBalance_MovesMoneyAndDecrementsStock()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.Purchased, $"Expected Purchased, got {result}");
        if (result is PurchaseListingResult.Purchased purchased)
        {
            Assert.Equal(15m, purchased.TotalPaid);
            Assert.Equal(7, purchased.NewStock);
        }

        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(7, Assert.Single(found.Listings).Stock);
        }
    }

    [Fact]
    public async Task Purchase_WithInsufficientStock_ReturnsInsufficientStockAndChargesNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 2);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        // Deposits carry the bank's own transaction fee (TransactionFeeBase + amount * TransactionFeeMultiplier),
        // same as withdrawals/transfers — so the balance right after depositing 100 isn't 100. Assert against
        // the deposit's own reported NewBalance rather than a hand-computed number, and confirm the purchase
        // below leaves it untouched (i.e. "charges nothing").
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var balanceAfterDeposit = deposited.NewBalance;

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 5, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.InsufficientStock, $"Expected InsufficientStock, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(buyerAccountId));
        Assert.True(accountDetails is BankAccountDetailsResult.Found, $"Expected Found, got {accountDetails}");
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.Equal(balanceAfterDeposit, found.BankAccount.Balance);
        }
    }

    [Fact]
    public async Task Purchase_WithInsufficientBalance_ReturnsInsufficientBalanceAndLeavesStockUnreserved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        // No deposit — buyer's balance stays 0.

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.InsufficientBalance, $"Expected InsufficientBalance, got {result}");

        // Reload the listing from Postgres (fresh query, not the in-memory variables from setup above)
        // to prove the stock reservation was never durably persisted in the first place — there's
        // nothing to "restore" since the cross-module transaction never committed.
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(10, Assert.Single(found.Listings).Stock);
        }
    }

    [Fact]
    public async Task Purchase_WithUnknownBuyerAccount_ReturnsBuyerAccountNotFoundAndLeavesStockUnreserved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, new BankAccountId(Guid.NewGuid())));

        Assert.True(result is PurchaseListingResult.BuyerAccountNotFound, $"Expected BuyerAccountNotFound, got {result}");

        // Reload the listing from Postgres (fresh query) to prove the stock reservation was never
        // durably persisted in the first place — there's nothing to "restore" since the cross-module
        // transaction never committed.
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(10, Assert.Single(found.Listings).Stock);
        }
    }

    [Fact]
    public async Task Purchase_WithNotAuthorizedBuyerAccount_LeavesStockAndBothBalancesUntouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);

        // Account owner (character A) — funds the account so a purchase would otherwise succeed.
        var accountOwnerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, accountOwnerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var buyerBalanceBeforePurchase = deposited.NewBalance;
        var payoutAccountId = await GetShopPayoutAccountIdAsync(mediator, shopId);
        var payoutBalanceBeforePurchase = await GetBalanceAsync(mediator, payoutAccountId);

        // Character B (a stranger) has no ownership or granted permission on A's personal account —
        // this reaches the same BankAccountAuthorization -> TransferOut(..., isAuthorized: false) path
        // as CorporateBankAccountTests.Withdraw_ByNonMemberCharacter_ReturnsNotAuthorized, but this
        // reservation has already succeeded by this point in the handler (stock was reserved before
        // TransferOut is called), so this is the test that proves the crash-window bug is fixed: a
        // failure *after* successful stock reservation must leave stock, buyer balance, and payout
        // balance all untouched.
        var strangerId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, strangerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.NotAuthorized, $"Expected NotAuthorized, got {result}");

        // All three reads below are fresh queries against Postgres, not in-memory state from setup.
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(10, Assert.Single(found.Listings).Stock);
        }

        var buyerBalanceAfterPurchase = await GetBalanceAsync(mediator, buyerAccountId);
        Assert.Equal(buyerBalanceBeforePurchase, buyerBalanceAfterPurchase);

        var payoutBalanceAfterPurchase = await GetBalanceAsync(mediator, payoutAccountId);
        Assert.Equal(payoutBalanceBeforePurchase, payoutBalanceAfterPurchase);
    }

    [Fact]
    public async Task Purchase_OnRemovedListing_ReturnsListingNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var removeResult = await mediator.Send(new RemoveListingCommand(shopId, listingId, sellerId));
        Assert.True(removeResult is RemoveListingResult.Removed, $"Expected Removed, got {removeResult}");

        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.ListingNotFound, $"Expected ListingNotFound, got {result}");
    }

    [Fact]
    public async Task Purchase_TwoConcurrentBuyersForLastUnit_ExactlyOneSucceeds()
    {
        var sellerScope = _provider.CreateAsyncScope();
        await using var _ = sellerScope;
        var setupMediator = sellerScope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(setupMediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(setupMediator, sellerId, price: 5m, stock: 1);

        var buyerAId = await CreateCharacterAsync(setupMediator);
        var buyerAAccountId = await OpenPersonalBankAccountAsync(setupMediator, buyerAId);
        await setupMediator.Send(new DepositCommand(buyerAAccountId, 100m));

        var buyerBId = await CreateCharacterAsync(setupMediator);
        var buyerBAccountId = await OpenPersonalBankAccountAsync(setupMediator, buyerBId);
        await setupMediator.Send(new DepositCommand(buyerBAccountId, 100m));

        // Deposits themselves carry a fee (Banking.Domain.BankAccount.CalculateFee, same formula
        // used below) — a 100m deposit does not net exactly 100m, so read the real post-deposit
        // balances back rather than hardcoding one, matching how Task 9's own tests were corrected.
        var preBalanceA = await GetBalanceAsync(setupMediator, buyerAAccountId);
        var preBalanceB = await GetBalanceAsync(setupMediator, buyerBAccountId);

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var purchaseA = mediatorA.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerAId, buyerAAccountId));
        var purchaseB = mediatorB.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerBId, buyerBAccountId));
        var results = await Task.WhenAll(purchaseA.AsTask(), purchaseB.AsTask());

        var purchasedCount = results.Count(r => r is PurchaseListingResult.Purchased);
        var losingResult = results.Single(r => r is not PurchaseListingResult.Purchased);
        Assert.Equal(1, purchasedCount);
        // The row lock (not Marten optimistic concurrency) serializes concurrent purchases, so the
        // losing purchase deterministically observes insufficient stock once the winner commits and
        // releases the lock — ListingChangedConcurrently is no longer reachable via this path (see
        // PurchaseListingResult.ListingChangedConcurrently's doc comment).
        Assert.True(
            losingResult is PurchaseListingResult.InsufficientStock,
            $"Expected the losing purchase to be InsufficientStock, got {losingResult}");

        var shopQuery = await setupMediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(0, Assert.Single(found.Listings).Stock);
        }

        // `PurchaseListingResult.Purchased` is a union case, not a subtype of `PurchaseListingResult`
        // (see ARCHITECTURE.md §9e) — `OfType<T>`'s `isinst` check against the CLR type always yields
        // an empty sequence for these, so extract via the compiler-special-cased `is`/`is not` pattern
        // this file already uses elsewhere, not LINQ's generic type filter.
        var winningResult = results.Single(r => r is PurchaseListingResult.Purchased);
        if (winningResult is not PurchaseListingResult.Purchased purchasedResult)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var winnerAccountId = results[0] is PurchaseListingResult.Purchased ? buyerAAccountId : buyerBAccountId;
        var loserAccountId = results[0] is PurchaseListingResult.Purchased ? buyerBAccountId : buyerAAccountId;
        var preBalanceWinner = results[0] is PurchaseListingResult.Purchased ? preBalanceA : preBalanceB;
        var preBalanceLoser = results[0] is PurchaseListingResult.Purchased ? preBalanceB : preBalanceA;

        var winnerBalance = await GetBalanceAsync(setupMediator, winnerAccountId);
        var loserBalance = await GetBalanceAsync(setupMediator, loserAccountId);

        // Same fee formula as Banking.Domain.BankAccount.CalculateFee, using the fixed 0.20/0.02
        // parameters every OpenBankAsync call in this test file passes to OpenBankCommand.
        var expectedFee = 0.20m + (purchasedResult.TotalPaid * 0.02m);
        Assert.Equal(preBalanceWinner - purchasedResult.TotalPaid - expectedFee, winnerBalance);
        Assert.Equal(preBalanceLoser, loserBalance);
    }

    [Fact]
    public async Task PurchaseListing_StillCommitsShopAndBankingAtomically()
    {
        // Regression guard for removing conjoined tenancy from Banking, Companies, and Shops:
        // PurchaseListingCommand spans the Shops and Banking stores through one
        // ICrossModuleTransaction — a shared Postgres transaction holding one tenanted and one
        // untenanted Marten session would silently break atomicity, so this proves both sessions
        // still join the same transaction and commit together now that neither is tenanted.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var balanceBefore = deposited.NewBalance;

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.Purchased, $"Expected Purchased, got {result}");
        if (result is not PurchaseListingResult.Purchased purchased)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        // Shops side committed: stock decremented and durably persisted (fresh query, not the
        // in-memory NewStock the handler returned).
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(7, Assert.Single(found.Listings).Stock);
        }

        // Banking side committed: buyer balance debited by TotalPaid plus TransferOut's own fee
        // (same formula as Banking.Domain.BankAccount.CalculateFee, using the fixed 0.20/0.02
        // parameters every OpenBankAsync call in this test file passes to OpenBankCommand).
        var expectedFee = 0.20m + (purchased.TotalPaid * 0.02m);
        var balanceAfter = await GetBalanceAsync(mediator, buyerAccountId);
        Assert.Equal(balanceBefore - purchased.TotalPaid - expectedFee, balanceAfter);
    }

    [Fact]
    public async Task TwoConcurrentPurchases_BySameBuyer_AgainstDifferentListings_ExactlyOneSucceedsIfBalanceInsufficientForBoth()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingAId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 60m, stock: 10);
        var (_, listingBId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 60m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        // Two concurrent purchases from the same buyer account, each costing 60 against a ~100
        // balance — only one can succeed without overdrawing.
        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new PurchaseListingCommand(shopId, listingAId, 1, buyerId, buyerAccountId));
            }),
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new PurchaseListingCommand(shopId, listingBId, 1, buyerId, buyerAccountId));
            }));

        var succeeded = results.Count(r => r is PurchaseListingResult.Purchased);
        Assert.Equal(1, succeeded);
    }

    // Task 6: PurchaseListingHandler grants the purchased item into World atomically with the
    // payment. These tests cover the grant itself; the tests above already cover the payment/stock
    // legs in isolation.

    [Fact]
    public async Task PurchaseListing_GrantsAnItemInstanceToTheBuyer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var listingItemId = await GetListingItemIdAsync(mediator, shopId, listingId);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerId, buyerAccountId));

        if (result is not PurchaseListingResult.Purchased purchased)
        {
            throw new InvalidOperationException($"Expected Purchased, got {result}");
        }

        var granted = Assert.Single(purchased.GrantedInstances);
        Assert.Equal(listingItemId, granted.ItemId);

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.FindByIdAsync(granted.InstanceId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(buyerId, stored.RootCharacterId);
        Assert.Equal(listingItemId, stored.ItemId);
    }

    /// <summary>
    /// Whole-branch review, I2. This test used to drive an <b>uncatalogued</b> listing, which
    /// <c>PurchaseListingHandler</c> rejects at its catalog precheck — before
    /// <c>transactionFactory.BeginAsync</c> is ever called. Nothing had been appended, so nothing rolled
    /// back: it asserted an untouched balance against a code path that never touched one, duplicating
    /// <see cref="PurchaseListing_ForAnUncataloguedListingItem_IsRejectedBeforeAnyPaymentMoves"/> below
    /// while claiming to prove the opposite. Nothing tested that the handler's in-transaction sequencing
    /// — which this branch changed — actually rolls back.
    ///
    /// So it now faults <i>inside</i> the open transaction, via a swapped-in
    /// <see cref="IItemInstanceRepositoryFactory"/>, exactly the way
    /// World.IntegrationTests/GatherTests.cs proves the gathering path's rollback. The fault sits in the
    /// fake's <c>SaveChangesAsync</c>, not its <c>GrantAsync</c>: the handler defers every leg's flush
    /// until all the in-memory work is queued, so a fault in <c>GrantAsync</c> would fire before the
    /// listing and bank legs flush and prove only "nothing flushes at all". Throwing from the item leg's
    /// flush means the listing's stock decrement and both bank legs have already durably written into
    /// the still-open, uncommitted transaction — which is the scenario under review.
    /// </summary>
    [Fact]
    public async Task PurchaseListing_WhenTheGrantFails_RollsBackThePayment()
    {
        await using var provider = TestServices.BuildProvider(configureServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IItemInstanceRepositoryFactory>(_ => new FaultyItemInstanceRepositoryFactory())));

        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100m));
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var balanceBeforePurchase = deposited.NewBalance;
        var payoutAccountId = await GetShopPayoutAccountIdAsync(mediator, shopId);
        var payoutBalanceBeforePurchase = await GetBalanceAsync(mediator, payoutAccountId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, buyerAccountId)).AsTask());

        // Every leg was written into an uncommitted ICrossModuleTransaction; disposing it without
        // committing rolls all of them back. Fresh reads, not the in-memory aggregates the handler
        // mutated — this has to prove the rollback reached Postgres.
        Assert.Equal(balanceBeforePurchase, await GetBalanceAsync(mediator, buyerAccountId));
        Assert.Equal(payoutBalanceBeforePurchase, await GetBalanceAsync(mediator, payoutAccountId));

        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        if (shopQuery is not ShopQueryResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {shopQuery}");
        }

        Assert.Equal(10, Assert.Single(found.Listings).Stock);
    }

    /// <summary>
    /// The uncatalogued-listing case this file used to conflate with the rollback test above: a listing
    /// whose <c>ItemId</c> no longer resolves is rejected at the precheck, <b>before</b> any transaction
    /// opens — which is the point, since no payment may move for an order that cannot be fulfilled.
    /// </summary>
    [Fact]
    public async Task PurchaseListing_ForAnUncataloguedListingItem_IsRejectedBeforeAnyPaymentMoves()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId) = await OpenShopWithUncatalogedListingAsync(scope.ServiceProvider, mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100m));
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var balanceBeforePurchase = deposited.NewBalance;

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 3, buyerId, buyerAccountId));

        Assert.True(result is PurchaseListingResult.ItemNotInCatalog, $"Expected ItemNotInCatalog, got {result}");

        // No transaction was ever opened for this request, so nothing had to roll back — both the
        // buyer's balance and the listing's stock are simply untouched.
        var balanceAfterPurchase = await GetBalanceAsync(mediator, buyerAccountId);
        Assert.Equal(balanceBeforePurchase, balanceAfterPurchase);

        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Equal(10, Assert.Single(found.Listings).Stock);
        }
    }

    [Fact]
    public async Task PurchaseListing_GrantsInstancesMarkedPendingSpawn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerId, buyerAccountId));

        if (result is not PurchaseListingResult.Purchased purchased)
        {
            throw new InvalidOperationException($"Expected Purchased, got {result}");
        }

        var granted = Assert.Single(purchased.GrantedInstances);
        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.FindByIdAsync(granted.InstanceId, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.True(stored.PendingSpawn);
        Assert.Null(stored.RootGameServerId);
        Assert.Equal(0, stored.Revision);
    }

    [Fact]
    public async Task PurchaseListing_ForQuantityOfTen_GrantsTenDiscreteInstances()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 20);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 1000m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 10, buyerId, buyerAccountId));

        if (result is not PurchaseListingResult.Purchased purchased)
        {
            throw new InvalidOperationException($"Expected Purchased, got {result}");
        }

        // Ten discrete rows, never a stack of ten — see World.Domain.Items.ItemInstance's class summary.
        Assert.Equal(10, purchased.GrantedInstances.Count);
        Assert.Equal(10, purchased.GrantedInstances.Select(x => x.InstanceId).Distinct().Count());

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.LoadManyAsync(
            purchased.GrantedInstances.Select(x => x.InstanceId).ToList(), CancellationToken.None);
        Assert.Equal(10, stored.Count);
        Assert.All(stored, x => Assert.Equal(buyerId, x.RootCharacterId));
    }

    [Fact]
    public async Task PurchaseListing_ExceedingMaxInstancesPerGrant_IsRejectedBeforeAnyPaymentMoves()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var settings = await mediator.Send(new ELifeRPG.World.Application.Settings.WorldSettingsQuery());
        var tooMany = settings.MaxInstancesPerGrant + 1;

        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 1m, stock: tooMany + 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(buyerAccountId, 100_000m));
        if (depositResult is not DepositResult.Deposited deposited)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var balanceBeforePurchase = deposited.NewBalance;

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, tooMany, buyerId, buyerAccountId));

        if (result is not PurchaseListingResult.GrantTooLarge grantTooLarge)
        {
            throw new InvalidOperationException($"Expected GrantTooLarge, got {result}");
        }

        Assert.Equal(tooMany, grantTooLarge.Requested);
        Assert.Equal(settings.MaxInstancesPerGrant, grantTooLarge.MaxInstancesPerGrant);

        // The cap is checked at the precheck, before transactionFactory.BeginAsync — no transaction
        // was ever opened for this request, so both the buyer's balance and the listing's stock must
        // be completely untouched.
        var balanceAfterPurchase = await GetBalanceAsync(mediator, buyerAccountId);
        Assert.Equal(balanceBeforePurchase, balanceAfterPurchase);

        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        if (shopQuery is not ShopQueryResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {shopQuery}");
        }

        Assert.Equal(tooMany + 10, Assert.Single(found.Listings).Stock);
    }

    [Fact]
    public async Task PurchaseListing_GrantsInstancesCarryingTheOriginatingListingReference()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sellerId = await CreateCharacterAsync(mediator);
        var (shopId, listingId, _) = await OpenShopWithListingAsync(mediator, sellerId, price: 5m, stock: 10);
        var buyerId = await CreateCharacterAsync(mediator);
        var buyerAccountId = await OpenPersonalBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(buyerAccountId, 100m));

        var result = await mediator.Send(new PurchaseListingCommand(shopId, listingId, 1, buyerId, buyerAccountId));

        if (result is not PurchaseListingResult.Purchased purchased)
        {
            throw new InvalidOperationException($"Expected Purchased, got {result}");
        }

        var granted = Assert.Single(purchased.GrantedInstances);
        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.FindByIdAsync(granted.InstanceId, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(ItemOrigin.ShopPurchase, stored.Origin);
        Assert.Equal(new OriginRef("Shops", listingId.Value.ToString()), stored.OriginRef);
    }

    // Accounts come from portal signup now, not from joining the gameserver:
    // CreateSessionCommand no longer creates one. See TestAccounts.
    private async Task<AccountId> CreateActiveAccountAsync()
    {
        using var scope = _provider.CreateScope();
        return (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync();
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Purchase Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private static async Task<BankId> OpenBankAsync(IMediator mediator)
    {
        var result = await mediator.Send(new OpenBankCommand("Purchase Test Bank", 0.20m, 0.02m));
        return result.Id;
    }

    private async Task<BankAccountId> OpenPersonalBankAccountAsync(IMediator mediator, CharacterId characterId)
    {
        var bankId = await OpenBankAsync(mediator);
        var result = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }

    private async Task<ItemId> CreateItemAsync(IMediator mediator)
    {
        // Prefab class names are unique across the catalog, and these tests run repeatedly against a
        // long-lived database, so mint a fresh one per call rather than colliding with the last run.
        var result = await mediator.Send(new CreateItemCommand("9mm Ammo Box", $"ELRPG_Test_AmmoBox_{Guid.NewGuid():N}"));

        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.ItemId;
    }

    private async Task<(ShopId ShopId, ShopListingId ListingId, CharacterId SellerId)> OpenShopWithListingAsync(
        IMediator mediator, CharacterId sellerId, decimal price, int stock)
    {
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, sellerId);
        var openResult = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, sellerId, null, "Purchase Test Shop", payoutAccountId));
        Assert.True(openResult is OpenShopResult.Opened, $"Expected Opened, got {openResult}");
        if (openResult is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var itemId = await CreateItemAsync(mediator);
        var addResult = await mediator.Send(new AddListingCommand(opened.ShopId, itemId, price, stock, sellerId));
        Assert.True(addResult is AddListingResult.Added, $"Expected Added, got {addResult}");
        if (addResult is not AddListingResult.Added added)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return (opened.ShopId, added.ListingId, sellerId);
    }

    private static async Task<ItemId> GetListingItemIdAsync(IMediator mediator, ShopId shopId, ShopListingId listingId)
    {
        var result = await mediator.Send(new ShopQuery(shopId));
        if (result is not ShopQueryResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {result}");
        }

        return found.Listings.Single(x => x.Id == listingId).ItemId;
    }

    /// <summary>
    /// Opens a shop and starts a listing stream directly through <see cref="IShopListingRepository"/>,
    /// bypassing <c>AddListingCommand</c>'s <c>ItemLookupQuery</c> validation on purpose — the listing's
    /// <c>ItemId</c> is never registered in the Items catalog at all. Stands in for "the catalog entry
    /// a listing was created against no longer exists by purchase time" (task 6's
    /// <c>ItemNotInCatalogException</c> path), since there is no item-deletion command to make a
    /// previously-valid <c>ItemId</c> stop resolving after the fact. Same direct-repository pattern
    /// <c>TestAccounts.CreateAsync</c> uses to mint an account without going through a command.
    /// </summary>
    private async Task<(ShopId ShopId, ShopListingId ListingId)> OpenShopWithUncatalogedListingAsync(
        IServiceProvider services, IMediator mediator, CharacterId sellerId, decimal price, int stock)
    {
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, sellerId);
        var openResult = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, sellerId, null, "Purchase Test Shop", payoutAccountId));
        if (openResult is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException($"Expected Opened, got {openResult}");
        }

        var uncatalogedItemId = new ItemId(Guid.NewGuid());
        var listingId = new ShopListingId(Guid.NewGuid());
        var domainEvent = new ListingCreated(listingId, opened.ShopId, uncatalogedItemId, price, stock);
        var listing = ShopListing.Create(domainEvent);

        var listingRepository = services.GetRequiredService<IShopListingRepository>();
        listingRepository.StartStream(listing, domainEvent);
        await listingRepository.SaveChangesAsync(CancellationToken.None);

        return (opened.ShopId, listingId);
    }

    private static async Task<BankAccountId> GetShopPayoutAccountIdAsync(IMediator mediator, ShopId shopId)
    {
        var result = await mediator.Send(new ShopQuery(shopId));
        Assert.True(result is ShopQueryResult.Found, $"Expected Found, got {result}");
        if (result is not ShopQueryResult.Found found)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return found.Shop.PayoutBankAccountId;
    }

    private static async Task<decimal> GetBalanceAsync(IMediator mediator, BankAccountId bankAccountId)
    {
        var result = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(result is BankAccountDetailsResult.Found, $"Expected Found, got {result}");
        if (result is not BankAccountDetailsResult.Found found)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return found.BankAccount.Balance;
    }

    /// <summary>
    /// Hand-written fake, ported from World.IntegrationTests/GatherTests.cs — no mocking library in this
    /// repo (ARCHITECTURE.md §9e). Used only by
    /// <see cref="PurchaseListing_WhenTheGrantFails_RollsBackThePayment"/>.
    /// </summary>
    private sealed class FaultyItemInstanceRepositoryFactory : IItemInstanceRepositoryFactory
    {
        public IItemInstanceRepository CreateFor(ELifeRPG.Shared.Integration.Abstractions.CrossModuleSessionHandle handle)
            => new FaultyItemInstanceRepository();
    }

    /// <summary>
    /// Every member <c>PurchaseListingHandler</c> doesn't touch throws, so an accidental new dependency
    /// on this fake surfaces immediately rather than silently no-op'ing. See the covering test's doc
    /// comment for why the fault sits in <see cref="SaveChangesAsync"/> rather than in the grant.
    /// </summary>
    private sealed class FaultyItemInstanceRepository : IItemInstanceRepository
    {
        public ValueTask<ItemInstance?> FindByIdAsync(ItemInstanceId id, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindByRootCharacterAsync(CharacterId rootCharacterId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindCarriedByRootCharacterAsync(CharacterId rootCharacterId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindPendingByRootCharacterAsync(
            CharacterId rootCharacterId, int limit, int maxDeliveryAttempts, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> LoadManyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindChildrenAsync(ItemInstanceId containerInstanceId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindUndeliverableAsync(int maxDeliveryAttempts, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public void Store(ItemInstance instance)
        {
            // Unreachable — this fake's GrantAsync never queues anything — but a void method has no
            // meaningful "not supported" signal, so it is a no-op rather than a throw.
        }

        public void RecordDeliveryAttempt(ItemInstance instance, DateTimeOffset now)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public void RecordSpawnFailure(ItemInstance instance, SpawnFailureReason reason, DateTimeOffset now)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public void Eject(ItemInstance instance)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        public void SoftDelete(ItemInstance instance)
            => throw new NotSupportedException("Not exercised by PurchaseListingHandler.");

        // The fault: fires only once the listing leg and both bank legs have already flushed into the
        // open, uncommitted cross-module transaction.
        public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Simulated failure in the item-grant leg's flush, after the listing and bank legs' own SaveChangesAsync already ran.");

        public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
            ItemId itemId, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("PurchaseListingHandler only ever uses the prefab-taking overload below.");

        // The prefab-taking overload PurchaseListingHandler actually calls — succeeds (pure in-memory,
        // matching the real repository's own "no I/O here" contract) so the handler proceeds to every
        // leg's SaveChangesAsync, where the fault above actually fires.
        public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
            ItemId itemId, string? prefabClassName, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
        {
            IReadOnlyList<GrantedInstance> granted = Enumerable.Range(0, quantity)
                .Select(_ => new GrantedInstance(new ItemInstanceId(Guid.NewGuid()), itemId, prefabClassName ?? "Test_Faulty"))
                .ToList();
            return ValueTask.FromResult(granted);
        }
    }
}
