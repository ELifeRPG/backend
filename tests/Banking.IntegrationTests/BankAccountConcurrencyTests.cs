using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Banking.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) — see README.md. Proves the
/// FetchForUpdateAsync/BankAccountConcurrencyException change actually serializes two concurrent
/// single-module writes against the same account, closing the gap the 2026-08-19 review found:
/// before this change, WithdrawHandler used a plain FindByIdAsync + unversioned Events.Append with
/// no concurrency check at all.
/// </summary>
public sealed class BankAccountConcurrencyTests : IAsyncLifetime
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
    public async Task TwoConcurrentWithdrawals_AgainstSameAccount_ExactlyOneSucceeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var bankId = (await mediator.Send(new OpenBankCommand("Concurrency Test Bank", 0.20m, 0.02m))).Id;
        var openResult = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));
        Assert.True(openResult is OpenBankAccountResult.Opened, $"Expected Opened, got {openResult}");
        var bankAccountId = openResult is OpenBankAccountResult.Opened opened ? opened.BankAccountId : throw new InvalidOperationException("Unreachable.");
        await mediator.Send(new DepositCommand(bankAccountId, 100m));

        // Only one 80-unit withdrawal can succeed against a ~100 balance; a race that loses the
        // concurrency check would let both succeed and drive the balance negative.
        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new WithdrawCommand(bankAccountId, characterId, 80m));
            }),
            Task.Run(async () =>
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                return await innerMediator.Send(new WithdrawCommand(bankAccountId, characterId, 80m));
            }));

        var succeeded = results.Count(r => r is WithdrawResult.Withdrawn);
        var conflicted = results.Count(r => r is WithdrawResult.ConcurrentModification or WithdrawResult.InsufficientBalance);

        Assert.Equal(1, succeeded);
        Assert.Equal(1, conflicted);

        var details = await mediator.Send(new BankAccountDetailsQuery(bankAccountId));
        Assert.True(details is BankAccountDetailsResult.Found, $"Expected Found, got {details}");
        if (details is BankAccountDetailsResult.Found found)
        {
            Assert.True(found.BankAccount.Balance >= 0m, "Balance must never go negative.");
        }
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var session = await mediator.Send(new CreateSessionCommand(new GameId(Guid.NewGuid()), "gameserver-dev"));
        _createdUsernames.Add(session.KeycloakUsername);
        var result = await mediator.Send(new CreateCharacterCommand(session.AccountId, "Concurrency Test Character"));
        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }
}
