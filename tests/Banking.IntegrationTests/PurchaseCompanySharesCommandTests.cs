using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Application.Companies;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Proves PurchaseCompanySharesCommand's cross-module atomicity: each
/// non-happy-path test reloads state from Postgres afterward to confirm nothing was partially
/// persisted, not just that an error was returned. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public sealed class PurchaseCompanySharesCommandTests : IAsyncLifetime
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
    public async Task Purchase_WithSufficientBalance_DebitsBuyerAndIssuesShares()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(bankAccountId, 1000m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        var balanceAfterDeposit = depositResult is DepositResult.Deposited deposited ? deposited.NewBalance : 0m;
        var companyId = await CreateCompanyAsync(mediator, buyerId);

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyId, 10, 5m));

        Assert.True(result is PurchaseCompanySharesResult.Purchased, $"Expected Purchased, got {result}");

        // Same fee formula as Banking.Domain.BankAccount.CalculateFee, using the fixed 0.20/0.02
        // parameters every OpenBankCommand call in this test file passes (see OpenBankAccountAsync
        // below). Computed against balanceAfterDeposit (not the raw 1000m deposit) because Deposit
        // itself charges a fee — see PurchaseListingTests for the identical pattern in Shops.
        const decimal totalPrice = 10 * 5m;
        var expectedFee = 0.20m + (totalPrice * 0.02m);
        var expectedBalance = balanceAfterDeposit - totalPrice - expectedFee;

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(accountDetails is BankAccountDetailsResult.Found, $"Expected Found, got {accountDetails}");
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.Equal(expectedBalance, found.BankAccount.Balance);
        }

        var companyDetails = await mediator.Send(new CompanyDetailsQuery(companyId));
        Assert.True(companyDetails is CompanyDetailsResult.Found, $"Expected Found, got {companyDetails}");
        if (companyDetails is CompanyDetailsResult.Found companyFound)
        {
            Assert.Contains(companyFound.Company.Shares, s => s.CharacterId == buyerId && s.Quantity == 10);
        }
    }

    [Fact]
    public async Task Purchase_WithInsufficientBalance_LeavesBankAccountAndCompanyUnchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(bankAccountId, 10m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        var balanceAfterDeposit = depositResult is DepositResult.Deposited deposited ? deposited.NewBalance : 0m;
        var companyId = await CreateCompanyAsync(mediator, buyerId);

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyId, 1000, 5m));

        Assert.True(result is PurchaseCompanySharesResult.InsufficientBalance, $"Expected InsufficientBalance, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            // Compared against the post-deposit balance (not the raw deposit amount) because
            // Deposit itself charges a fee (see BankAccount.Deposit) — this only needs to prove
            // the failed purchase left the balance untouched, not restate the deposit's own math.
            Assert.Equal(balanceAfterDeposit, found.BankAccount.Balance);
        }

        var companyDetails = await mediator.Send(new CompanyDetailsQuery(companyId));
        if (companyDetails is CompanyDetailsResult.Found companyFound)
        {
            Assert.Empty(companyFound.Company.Shares);
        }
    }

    [Fact]
    public async Task Purchase_ForUnknownCompany_LeavesBankAccountUnchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(bankAccountId, 1000m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        var balanceAfterDeposit = depositResult is DepositResult.Deposited deposited ? deposited.NewBalance : 0m;

        var result = await mediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, new CompanyId(Guid.NewGuid()), 10, 5m));

        Assert.True(result is PurchaseCompanySharesResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            // Compared against the post-deposit balance (not the raw deposit amount) because
            // Deposit itself charges a fee (see BankAccount.Deposit) — this only needs to prove
            // the failed purchase left the balance untouched, not restate the deposit's own math.
            Assert.Equal(balanceAfterDeposit, found.BankAccount.Balance);
        }
    }

    [Fact]
    public async Task Purchase_WhenCompanySideFailsAfterBankingSideFlushed_RollsBackBankingWrite()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        var depositResult = await mediator.Send(new DepositCommand(bankAccountId, 1000m));
        Assert.True(depositResult is DepositResult.Deposited, $"Expected Deposited, got {depositResult}");
        var balanceAfterDeposit = depositResult is DepositResult.Deposited deposited ? deposited.NewBalance : 0m;
        await CreateCompanyAsync(mediator, buyerId);

        var transactionFactory = scope.ServiceProvider.GetRequiredService<ICrossModuleTransactionFactory>();
        var bankAccountRepositoryFactory = scope.ServiceProvider.GetRequiredService<IBankAccountRepositoryFactory>();

        // Manually reproduce PurchaseCompanySharesCommand's handler up through the Banking-side
        // SaveChangesAsync, then deliberately stop — no company-side write, no CommitAsync. Disposing
        // the transaction without committing must roll back the already-flushed BankAccountWithdrawn
        // event, proving the two repositories really share one Postgres transaction rather than each
        // auto-committing on its own connection.
        await using (var transaction = await transactionFactory.BeginAsync(CancellationToken.None))
        {
            var bankAccountRepository = bankAccountRepositoryFactory.CreateFor(transaction.Handle);
            var bankAccount = await bankAccountRepository.FindByIdAsync(bankAccountId, CancellationToken.None);
            Assert.NotNull(bankAccount);

            // buyerId owns this personal account, so it is trivially authorized — mirrors
            // BankAccountAuthorization.IsAuthorizedAsync's Personal-account branch without needing
            // that internal type (it's not visible from this test assembly).
            var withdrawnEvent = bankAccount!.Withdraw(buyerId, isAuthorized: true, 50m);
            bankAccountRepository.Append(bankAccountId, withdrawnEvent);

            await bankAccountRepository.SaveChangesAsync(CancellationToken.None);

            // Simulate a failure between the Banking-side flush and the Companies-side flush:
            // no company.IssueShares, no companyRepository.SaveChangesAsync, no transaction.CommitAsync.
            // `transaction` goes out of scope here via `await using` and must roll back.
        }

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(accountDetails is BankAccountDetailsResult.Found, $"Expected Found, got {accountDetails}");
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.Equal(balanceAfterDeposit, found.BankAccount.Balance);
        }
    }

    [Fact]
    public async Task TwoConcurrentPurchases_AgainstSameBankAccount_ExactlyOneSucceeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var buyerId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, buyerId);
        await mediator.Send(new DepositCommand(bankAccountId, 1000m));
        var companyAId = await CreateCompanyAsync(mediator, buyerId);
        var companyBId = await CreateCompanyAsync(mediator, buyerId);

        // Two concurrent purchases from different companies, both paid from the same bank account,
        // each costing ~600 against a ~1000 balance — only one can succeed without overdrawing.
        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyAId, 100, 6m));
            }),
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new PurchaseCompanySharesCommand(bankAccountId, buyerId, companyBId, 100, 6m));
            }));

        var succeeded = results.Count(r => r is PurchaseCompanySharesResult.Purchased);
        Assert.Equal(1, succeeded);

        var accountDetails = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(accountDetails is BankAccountDetailsResult.Found, $"Expected Found, got {accountDetails}");
        if (accountDetails is BankAccountDetailsResult.Found found)
        {
            Assert.True(found.BankAccount.Balance >= 0m, "Balance must never go negative.");
        }
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
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Shares Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private static async Task<BankId> OpenBankAsync(IMediator mediator)
    {
        var result = await mediator.Send(new OpenBankCommand("Test Bank", 0.20m, 0.02m));
        return result.Id;
    }

    private async Task<BankAccountId> OpenBankAccountAsync(IMediator mediator, CharacterId characterId)
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

    private static async Task<CompanyId> CreateCompanyAsync(IMediator mediator, CharacterId founderCharacterId)
    {
        var result = await mediator.Send(new CreateCompanyCommand("Test Company", founderCharacterId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CompanyId;
    }
}
