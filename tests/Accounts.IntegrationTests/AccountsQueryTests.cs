using ELifeRPG.Accounts.Application.Accounts;
using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class AccountsQueryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
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

    private async Task<(AccountId AccountId, GameId BohemiaId)> CreateAccountAsync()
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var response = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        _createdUsernames.Add(response.KeycloakUsername);
        return (response.AccountId, bohemiaId);
    }

    [Fact]
    public async Task Handle_EmptySearch_IncludesEveryAccount()
    {
        var (accountId, _) = await CreateAccountAsync();

        var result = await Send<AccountsQuery, AccountsResult>(new AccountsQuery(string.Empty));

        if (result is not AccountsResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {result}.");
        }
        Assert.Contains(found.Accounts, a => a.Id == accountId);
    }

    [Fact]
    public async Task Handle_SearchMatchingBohemiaIdSubstring_ReturnsOnlyMatchingAccounts()
    {
        var (accountId, bohemiaId) = await CreateAccountAsync();
        var searchTerm = bohemiaId.Value.ToString()[..8];

        var result = await Send<AccountsQuery, AccountsResult>(new AccountsQuery(searchTerm));

        if (result is not AccountsResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {result}.");
        }
        Assert.Contains(found.Accounts, a => a.Id == accountId);
        Assert.All(found.Accounts, a => Assert.Contains(searchTerm, a.BohemiaId.Value.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handle_SearchMatchingNothing_ReturnsEmpty()
    {
        await CreateAccountAsync();

        var result = await Send<AccountsQuery, AccountsResult>(new AccountsQuery("no-such-bohemia-id-substring"));

        if (result is not AccountsResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {result}.");
        }
        Assert.Empty(found.Accounts);
    }

    private async Task<TResponse> Send<TCommand, TResponse>(TCommand command) where TCommand : IRequest<TResponse>
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
