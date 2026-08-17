using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Shops;
using ELifeRPG.Shops.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class PurchaseListingTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var username in _createdUsernames)
        {
            await _keycloak.DeleteUserAsync(username);
        }

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

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync(mediator);
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
        var result = await mediator.Send(new CreateItemCommand("9mm Ammo Box", "Ammo_9x19_Box"));

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
}
