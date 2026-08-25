using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Exercises Banking's cross-module authorization path into Companies
/// (BankAccountAuthorization -> CompanyMemberPermissionsQuery) against live Postgres, plus the
/// BankAccountsByCharacterQuery/BankAccountsByCompanyQuery nullable-owner-id LINQ filters, which no
/// other test exercises.
/// </summary>
public sealed class CorporateBankAccountTests : IAsyncLifetime
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
    public async Task OpenCorporateBankAccount_ForKnownCompany_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, _) = await CreateCompanyWithFounderAsync(mediator);
        var bankId = await OpenBankAsync(mediator);

        var result = await mediator.Send(new OpenCorporateBankAccountCommand(bankId, companyId));

        Assert.True(result is OpenCorporateBankAccountResult.Opened, $"Expected Opened, got {result}");
    }

    [Fact]
    public async Task OpenCorporateBankAccount_ForUnknownCompany_ReturnsCompanyNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bankId = await OpenBankAsync(mediator);

        var result = await mediator.Send(new OpenCorporateBankAccountCommand(bankId, new CompanyId(Guid.NewGuid())));

        Assert.True(result is OpenCorporateBankAccountResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");
    }

    [Fact]
    public async Task OpenCorporateBankAccount_ForUnknownBank_ReturnsBankNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, _) = await CreateCompanyWithFounderAsync(mediator);

        var result = await mediator.Send(new OpenCorporateBankAccountCommand(new BankId(Guid.NewGuid()), companyId));

        Assert.True(result is OpenCorporateBankAccountResult.BankNotFound, $"Expected BankNotFound, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByFounderWithOwnerPosition_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, founderId) = await CreateCompanyWithFounderAsync(mediator);
        var bankAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, founderId, 50m));

        Assert.True(result is WithdrawResult.Withdrawn, $"Expected Withdrawn, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByRookieMemberWithoutFinancePermission_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, _) = await CreateCompanyWithFounderAsync(mediator);
        var bankAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

        var rookieId = await CreateCharacterAsync(mediator);
        var addMemberResult = await mediator.Send(new AddMemberCommand(companyId, rookieId));
        Assert.True(addMemberResult is AddMemberResult.Added, $"Expected Added, got {addMemberResult}");

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, rookieId, 10m));

        Assert.True(result is WithdrawResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task Withdraw_ByNonMemberCharacter_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, _) = await CreateCompanyWithFounderAsync(mediator);
        var bankAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

        var strangerId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new WithdrawCommand(bankAccountId, strangerId, 10m));

        Assert.True(result is WithdrawResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task Transfer_FromCorporateAccountByOwner_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, founderId) = await CreateCompanyWithFounderAsync(mediator);
        var corporateAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);
        await mediator.Send(new DepositCommand(corporateAccountId, 100m));
        var personalAccountId = await OpenPersonalBankAccountAsync(mediator, founderId);

        var result = await mediator.Send(new TransferCommand(corporateAccountId, personalAccountId, founderId, 40m));

        Assert.True(result is TransferResult.Transferred, $"Expected Transferred, got {result}");
    }

    [Fact]
    public async Task BankAccountsByCompanyQuery_ReturnsOnlyThatCompanysAccounts()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, _) = await CreateCompanyWithFounderAsync(mediator);
        var corporateAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);

        var (otherCompanyId, _) = await CreateCompanyWithFounderAsync(mediator);
        await OpenCorporateBankAccountAsync(mediator, otherCompanyId);

        var accounts = await mediator.Send(new BankAccountsByCompanyQuery(companyId));

        Assert.Single(accounts);
        Assert.Equal(corporateAccountId, accounts[0].Id);
    }

    [Fact]
    public async Task BankAccountsByCharacterQuery_DoesNotIncludeCorporateAccounts()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, founderId) = await CreateCompanyWithFounderAsync(mediator);
        await OpenCorporateBankAccountAsync(mediator, companyId);
        var personalAccountId = await OpenPersonalBankAccountAsync(mediator, founderId);

        var accounts = await mediator.Send(new BankAccountsByCharacterQuery(founderId));

        Assert.Single(accounts);
        Assert.Equal(personalAccountId, accounts[0].Id);
        Assert.Equal(BankAccountType.Personal, accounts[0].Type);
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
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Corporate Bank Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private async Task<(CompanyId CompanyId, CharacterId FounderId)> CreateCompanyWithFounderAsync(IMediator mediator)
    {
        var founderId = await CreateCharacterAsync(mediator);
        var result = await mediator.Send(new CreateCompanyCommand("Corporate Bank Test Corp", founderId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return (created.CompanyId, founderId);
    }

    private static async Task<BankId> OpenBankAsync(IMediator mediator)
    {
        var result = await mediator.Send(new OpenBankCommand("Corporate Test Bank", 0.20m, 0.02m));
        return result.Id;
    }

    private async Task<BankAccountId> OpenCorporateBankAccountAsync(IMediator mediator, CompanyId companyId)
    {
        var bankId = await OpenBankAsync(mediator);
        var result = await mediator.Send(new OpenCorporateBankAccountCommand(bankId, companyId));

        Assert.True(result is OpenCorporateBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenCorporateBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
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
}
