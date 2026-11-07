using ELifeRPG.Banking.Infrastructure.Common;
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class BankingCommandTests : IAsyncLifetime
{
    // Deposits record who handed over the cash, but never check it; any character id will do.
    private static readonly CharacterId Depositor = new(Guid.NewGuid());

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
    public async Task OpenBankAccount_ForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankId = BankSeed.FederalELifeBankId;

        var result = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
    }

    [Fact]
    public async Task OpenBankAccount_CarriesTheBanksFeeTermsOnTheAccount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new OpenBankAccountCommand(BankSeed.FederalELifeBankId, characterId));

        var opened = result is OpenBankAccountResult.Opened value ? value : throw new InvalidOperationException($"Expected Opened, got {result}");
        Assert.Equal(0.20m, opened.BankAccount.TransactionFeeBase);
        Assert.Equal(0.02m, opened.BankAccount.TransactionFeeMultiplier);

        var details = await mediator.Send(new BankAccountDetailsQuery(opened.BankAccountId));
        var found = details is BankAccountDetailsResult.Found f ? f : throw new InvalidOperationException($"Expected Found, got {details}");
        Assert.Equal(0.20m, found.BankAccount.TransactionFeeBase);
        Assert.Equal(0.02m, found.BankAccount.TransactionFeeMultiplier);
    }

    [Theory]
    [InlineData("0.25")]
    [InlineData("33.33")]
    [InlineData("50")]
    public async Task FeePreviewedFromTheAccountsTerms_EqualsTheBookedFee(string amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));
        var details = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        var account = details is BankAccountDetailsResult.Found f ? f.BankAccount : throw new InvalidOperationException($"Expected Found, got {details}");
        var value = decimal.Parse(amount);
        var preview = Money.RoundToCents(account.TransactionFeeBase + (value * account.TransactionFeeMultiplier));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, value));

        var withdrawn = result is WithdrawResult.Withdrawn w ? w : throw new InvalidOperationException($"Expected Withdrawn, got {result}");
        Assert.Equal(preview, withdrawn.Fee);
    }

    [Fact]
    public async Task OpenBankAccount_IssuesValidNumberUnderTheBanksCode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new OpenBankAccountCommand(BankSeed.FederalELifeBankId, characterId));

        var opened = result is OpenBankAccountResult.Opened value ? value : throw new InvalidOperationException($"Expected Opened, got {result}");
        Assert.True(BankAccountNumber.TryNormalize(opened.Number, out var normalized), opened.Number);
        Assert.Equal(opened.Number, normalized);
        Assert.Equal("FEBK", opened.Number[4..8]);
    }

    [Fact]
    public async Task Banks_IncludeTheSeededBankWithItsCode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var banks = await mediator.Send(new BanksQuery());

        var seeded = Assert.Single(banks, bank => bank.Id == BankSeed.FederalELifeBankId);
        Assert.Equal("FEBK", seeded.Code);
    }

    [Fact]
    public async Task BankAccountByNumber_AcceptsTheNumberAsTyped()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);
        var number = await GetNumberAsync(mediator, bankAccountId);
        var typed = string.Join(' ', number.Chunk(4).Select(chunk => new string(chunk))).ToLowerInvariant();

        var result = await mediator.Send(new BankAccountByNumberQuery(typed));

        var found = result is BankAccountByNumberResult.Found value ? value : throw new InvalidOperationException($"Expected Found, got {result}");
        Assert.Equal(bankAccountId, found.BankAccount.Id);
    }

    [Fact]
    public async Task BankAccountByNumber_WithBadCheckDigits_ReturnsInvalidNumber()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var number = await GetNumberAsync(mediator, await OpenBankAccountAsync(mediator));
        var lastDigit = number[^1] == '9' ? '0' : (char)(number[^1] + 1);

        var result = await mediator.Send(new BankAccountByNumberQuery(number[..^1] + lastDigit));

        Assert.True(result is BankAccountByNumberResult.InvalidNumber, $"Expected InvalidNumber, got {result}");
    }

    [Fact]
    public async Task BankAccountByNumber_ForUnissuedValidNumber_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Valid layout and check digits under a code no seeded bank uses, so it can't be issued.
        var result = await mediator.Send(new BankAccountByNumberQuery(BankAccountNumber.Create("ZZZZ", "0000000001")));

        Assert.True(result is BankAccountByNumberResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task OpenBankAccount_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankId = BankSeed.FederalELifeBankId;

        var result = await mediator.Send(new OpenBankAccountCommand(bankId, new CharacterId(Guid.NewGuid())));

        Assert.True(result is OpenBankAccountResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task OpenBankAccount_ForUnknownBank_ReturnsBankNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new OpenBankAccountCommand(new BankId(Guid.NewGuid()), characterId));

        Assert.True(result is OpenBankAccountResult.BankNotFound, $"Expected BankNotFound, got {result}");
    }

    [Fact]
    public async Task Deposit_IncreasesBalanceByAmountMinusFee()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);

        var result = await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));

        Assert.True(result is DepositResult.Deposited, $"Expected Deposited, got {result}");
        if (result is DepositResult.Deposited deposited)
        {
            Assert.Equal(100m - deposited.Fee, deposited.NewBalance);
        }
    }

    [Fact]
    public async Task Deposit_BooksTheFeeRoundedToWholeCents()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);

        // The seeded bank charges 0.20 + 2 %: 0.20 + 0.25 * 0.02 = 0.205, booked as 0.21.
        var result = await mediator.Send(new DepositCommand(bankAccountId, Depositor, 0.25m));

        var deposited = result is DepositResult.Deposited value ? value : throw new InvalidOperationException($"Expected Deposited, got {result}");
        Assert.Equal(0.21m, deposited.Fee);
        Assert.Equal(0.04m, deposited.NewBalance);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.005")]
    public async Task Deposit_WithInvalidAmount_ReturnsInvalidAmount(string amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);

        var result = await mediator.Send(new DepositCommand(bankAccountId, Depositor, decimal.Parse(amount)));

        Assert.True(result is DepositResult.InvalidAmount, $"Expected InvalidAmount, got {result}");
    }

    [Fact]
    public async Task Withdraw_WithFractionalCentAmount_ReturnsInvalidAmount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, 1.005m));

        Assert.True(result is WithdrawResult.InvalidAmount, $"Expected InvalidAmount, got {result}");
    }

    [Fact]
    public async Task Transfer_ToTheSameAccount_ReturnsSameAccountAndChangesNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        var deposit = await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));
        var balanceBefore = deposit is DepositResult.Deposited d ? d.NewBalance : throw new InvalidOperationException($"Expected Deposited, got {deposit}");

        var result = await mediator.Send(new TransferCommand(bankAccountId, bankAccountId, characterId, 10m));

        Assert.True(result is TransferResult.SameAccount, $"Expected SameAccount, got {result}");
        var details = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        var found = details is BankAccountDetailsResult.Found f ? f : throw new InvalidOperationException($"Expected Found, got {details}");
        Assert.Equal(balanceBefore, found.BankAccount.Balance);
    }

    [Fact]
    public async Task Transfer_WithFractionalCentAmount_ReturnsInvalidAmount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(sourceAccountId, Depositor, 100m));

        var result = await mediator.Send(new TransferCommand(sourceAccountId, targetAccountId, characterId, 1.005m));

        Assert.True(result is TransferResult.InvalidAmount, $"Expected InvalidAmount, got {result}");
    }

    [Fact]
    public async Task Deposit_LedgerRowCarriesItsTransactionIdDepositorGameServerAndSource()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);
        var gameServerId = await scope.ServiceProvider.GetRequiredService<ELifeRPG.Banking.Application.Common.ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m, "atm-7"));

        var deposited = result is DepositResult.Deposited value ? value : throw new InvalidOperationException($"Expected Deposited, got {result}");
        var history = await mediator.Send(new BankAccountTransactionHistoryQuery(bankAccountId));
        var found = history is BankAccountTransactionHistoryResult.Found f ? f : throw new InvalidOperationException($"Expected Found, got {history}");
        var row = Assert.Single(found.Transactions);
        Assert.Equal(deposited.TransactionId, row.Booking.TransactionId);
        Assert.Equal(Depositor, row.ActingCharacterId);
        Assert.Equal(gameServerId, row.Booking.GameServerId);
        Assert.Equal("atm-7", row.Booking.Source);
    }

    [Fact]
    public async Task Transfer_BothLedgerRowsShareTheTransactionId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(sourceAccountId, Depositor, 100m));

        var result = await mediator.Send(new TransferCommand(sourceAccountId, targetAccountId, characterId, 10m, "terminal-2"));

        var transferred = result is TransferResult.Transferred value ? value : throw new InvalidOperationException($"Expected Transferred, got {result}");
        var outRow = await LatestRowAsync(mediator, sourceAccountId);
        var inRow = await LatestRowAsync(mediator, targetAccountId);
        Assert.Equal(BankAccountTransactionKind.TransferredOut, outRow.Kind);
        Assert.Equal(BankAccountTransactionKind.TransferredIn, inRow.Kind);
        Assert.Equal(transferred.TransactionId, outRow.Booking.TransactionId);
        Assert.Equal(transferred.TransactionId, inRow.Booking.TransactionId);
        Assert.Equal("terminal-2", inRow.Booking.Source);
    }

    [Fact]
    public async Task Deposit_WithoutCharacter_ReturnsMissingCharacterAndBooksNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);

        var result = await mediator.Send(new DepositCommand(bankAccountId, new CharacterId(Guid.Empty), 100m));

        Assert.True(result is DepositResult.MissingCharacter, $"Expected MissingCharacter, got {result}");
        var details = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        var found = details is BankAccountDetailsResult.Found f ? f : throw new InvalidOperationException($"Expected Found, got {details}");
        Assert.Equal(0m, found.BankAccount.Balance);
    }

    [Fact]
    public async Task Withdraw_AndTransfer_WithoutCharacter_AreNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));
        var nobody = new CharacterId(Guid.Empty);

        var withdrawn = await mediator.Send(new WithdrawCommand(bankAccountId, nobody, 10m));
        var transferred = await mediator.Send(new TransferCommand(bankAccountId, targetAccountId, nobody, 10m));

        Assert.True(withdrawn is WithdrawResult.NotAuthorized, $"Expected NotAuthorized, got {withdrawn}");
        Assert.True(transferred is TransferResult.NotAuthorized, $"Expected NotAuthorized, got {transferred}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Booking.MaxSourceLength + 1)]
    public async Task Withdraw_WithEmptyOrOverlongSource_ReturnsInvalidSource(int sourceLength)
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, 10m, new string('x', sourceLength)));

        Assert.True(result is WithdrawResult.InvalidSource, $"Expected InvalidSource, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByOwner_WithSufficientBalance_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, 50m));

        Assert.True(result is WithdrawResult.Withdrawn, $"Expected Withdrawn, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByNonOwner_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, new CharacterId(Guid.NewGuid()), 10m));

        Assert.True(result is WithdrawResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task Withdraw_WithInsufficientBalance_ReturnsInsufficientBalance()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, Depositor, 10m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, 10_000m));

        Assert.True(result is WithdrawResult.InsufficientBalance, $"Expected InsufficientBalance, got {result}");
    }

    [Fact]
    public async Task Transfer_BetweenTwoAccounts_MovesBalanceAtomically()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(sourceAccountId, Depositor, 100m));

        var result = await mediator.Send(new TransferCommand(sourceAccountId, targetAccountId, characterId, 40m));

        Assert.True(result is TransferResult.Transferred, $"Expected Transferred, got {result}");

        var sourceDetails = await mediator.Send(new BankAccountDetailsQuery(sourceAccountId));
        var targetDetails = await mediator.Send(new BankAccountDetailsQuery(targetAccountId));

        Assert.True(sourceDetails is BankAccountDetailsResult.Found, $"Expected Found, got {sourceDetails}");
        Assert.True(targetDetails is BankAccountDetailsResult.Found, $"Expected Found, got {targetDetails}");

        if (sourceDetails is BankAccountDetailsResult.Found sourceFound && targetDetails is BankAccountDetailsResult.Found targetFound)
        {
            Assert.Equal(40m, targetFound.BankAccount.Balance);
            Assert.True(sourceFound.BankAccount.Balance < 60m, "Source balance should be reduced by amount plus fee.");
        }
    }

    [Fact]
    public async Task TransactionHistory_ForUnknownAccount_ReturnsBankAccountNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new BankAccountTransactionHistoryQuery(new BankAccountId(Guid.NewGuid())));

        Assert.True(result is BankAccountTransactionHistoryResult.BankAccountNotFound, $"Expected BankAccountNotFound, got {result}");
    }

    [Fact]
    public async Task TransactionHistory_AfterDepositWithdrawAndTransfer_ReturnsAllNewestFirst()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);

        await mediator.Send(new DepositCommand(sourceAccountId, Depositor, 100m));
        await mediator.Send(new WithdrawCommand(sourceAccountId, characterId, 20m));
        await mediator.Send(new TransferCommand(sourceAccountId, targetAccountId, characterId, 10m));

        var result = await mediator.Send(new BankAccountTransactionHistoryQuery(sourceAccountId));

        Assert.True(result is BankAccountTransactionHistoryResult.Found, $"Expected Found, got {result}");
        if (result is not BankAccountTransactionHistoryResult.Found found)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(3, found.Transactions.Count);
        Assert.Equal(
            [BankAccountTransactionKind.TransferredOut, BankAccountTransactionKind.Withdrawn, BankAccountTransactionKind.Deposited],
            found.Transactions.Select(x => x.Kind));
        Assert.True(
            found.Transactions[0].OccurredAt >= found.Transactions[1].OccurredAt
            && found.Transactions[1].OccurredAt >= found.Transactions[2].OccurredAt,
            "Transactions should be ordered newest first.");

        var transferOut = found.Transactions[0];
        Assert.Equal(targetAccountId, transferOut.CounterpartyBankAccountId);
        Assert.Equal(await GetNumberAsync(mediator, targetAccountId), transferOut.CounterpartyNumber);
        Assert.Equal(characterId, transferOut.ActingCharacterId);
        Assert.All(found.Transactions.Skip(1), x => Assert.Null(x.CounterpartyNumber));
    }

    [Fact]
    public async Task TransactionHistory_TargetAccountSeesTransferredIn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(sourceAccountId, Depositor, 100m));
        await mediator.Send(new TransferCommand(sourceAccountId, targetAccountId, characterId, 10m));

        var result = await mediator.Send(new BankAccountTransactionHistoryQuery(targetAccountId));

        Assert.True(result is BankAccountTransactionHistoryResult.Found, $"Expected Found, got {result}");
        if (result is BankAccountTransactionHistoryResult.Found found)
        {
            var transferIn = Assert.Single(found.Transactions);
            Assert.Equal(BankAccountTransactionKind.TransferredIn, transferIn.Kind);
            Assert.Equal(10m, transferIn.Amount);
            Assert.Equal(0m, transferIn.Fee);
            Assert.Equal(sourceAccountId, transferIn.CounterpartyBankAccountId);
            Assert.Equal(await GetNumberAsync(mediator, sourceAccountId), transferIn.CounterpartyNumber);
        }
    }

    [Fact]
    public async Task BankAccount_OpenedOnOneServer_IsVisibleFromAnotherServer()
    {
        // Hive model: money follows the player across maps, so an account opened via one gameserver
        // must be reachable from another. Asserts the opposite of the pre-hive behaviour.
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediatorA);

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var detailsFromCreatingServer = await mediatorA.Send(new BankAccountDetailsQuery(bankAccountId));
        var detailsFromOtherServer = await mediatorB.Send(new BankAccountDetailsQuery(bankAccountId));

        Assert.True(detailsFromCreatingServer is BankAccountDetailsResult.Found, $"Expected Found from the creating server, got {detailsFromCreatingServer}");
        Assert.True(detailsFromOtherServer is BankAccountDetailsResult.Found, $"Expected Found from a different server, got {detailsFromOtherServer}");
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
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Bank Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private static async Task<BankAccountTransactionRecord> LatestRowAsync(IMediator mediator, BankAccountId bankAccountId)
    {
        var history = await mediator.Send(new BankAccountTransactionHistoryQuery(bankAccountId));
        return history is BankAccountTransactionHistoryResult.Found found
            ? found.Transactions[0]
            : throw new InvalidOperationException($"Expected Found, got {history}");
    }

    private static async Task<string> GetNumberAsync(IMediator mediator, BankAccountId bankAccountId)
    {
        var details = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        return details is BankAccountDetailsResult.Found found
            ? found.BankAccount.Number
            : throw new InvalidOperationException($"Expected Found, got {details}");
    }

    private async Task<BankAccountId> OpenBankAccountAsync(IMediator mediator, CharacterId? characterId = null)
    {
        var resolvedCharacterId = characterId ?? await CreateCharacterAsync(mediator);
        var bankId = BankSeed.FederalELifeBankId;

        var result = await mediator.Send(new OpenBankAccountCommand(bankId, resolvedCharacterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }
}
