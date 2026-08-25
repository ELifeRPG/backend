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
    public async Task OpenBankAccount_ForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankId = await OpenBankAsync(mediator);

        var result = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
    }

    [Fact]
    public async Task OpenBankAccount_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankId = await OpenBankAsync(mediator);

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

        var result = await mediator.Send(new DepositCommand(bankAccountId, 100m));

        Assert.True(result is DepositResult.Deposited, $"Expected Deposited, got {result}");
        if (result is DepositResult.Deposited deposited)
        {
            Assert.Equal(100m - deposited.Fee, deposited.NewBalance);
        }
    }

    [Fact]
    public async Task Withdraw_ByOwner_WithSufficientBalance_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankAccountId = await OpenBankAccountAsync(mediator, characterId);
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, characterId, 50m));

        Assert.True(result is WithdrawResult.Withdrawn, $"Expected Withdrawn, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByNonOwner_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

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
        await mediator.Send(new DepositCommand(bankAccountId, 10m));

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
        await mediator.Send(new DepositCommand(sourceAccountId, 100m));

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

        await mediator.Send(new DepositCommand(sourceAccountId, 100m));
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
        Assert.Equal(characterId, transferOut.ActingCharacterId);
    }

    [Fact]
    public async Task TransactionHistory_TargetAccountSeesTransferredIn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var sourceAccountId = await OpenBankAccountAsync(mediator, characterId);
        var targetAccountId = await OpenBankAccountAsync(mediator);
        await mediator.Send(new DepositCommand(sourceAccountId, 100m));
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
        }
    }

    [Fact]
    public async Task OpenBankAccount_ForBankOpenedOnAnotherServer_Succeeds()
    {
        // Hive model: banks are hive-wide, so a bank opened via one gameserver must be usable from
        // another. Asserts the opposite of the pre-hive behaviour — see
        // docs/superpowers/specs/2026-08-22-hive-tenancy-design.md.
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var bankId = await OpenBankAsync(mediatorA);

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();
        var characterOnServerB = await CreateCharacterAsync(mediatorB);

        var result = await mediatorB.Send(new OpenBankAccountCommand(bankId, characterOnServerB));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
    }

    [Fact]
    public async Task BankAccount_OpenedOnOneServer_IsVisibleFromAnotherServer()
    {
        // Hive model: money follows the player across maps, so an account opened via one gameserver
        // must be reachable from another. Asserts the opposite of the pre-hive behaviour — see
        // docs/superpowers/specs/2026-08-22-hive-tenancy-design.md.
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

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync(mediator);
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Bank Test Character"));

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

    private async Task<BankAccountId> OpenBankAccountAsync(IMediator mediator, CharacterId? characterId = null)
    {
        var resolvedCharacterId = characterId ?? await CreateCharacterAsync(mediator);
        var bankId = await OpenBankAsync(mediator);

        var result = await mediator.Send(new OpenBankAccountCommand(bankId, resolvedCharacterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }
}
