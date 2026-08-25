using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Application.Whitelist;
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
public sealed class SubmitWhitelistApplicationCommandTests : IAsyncLifetime
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

    private async Task<AccountId> CreateAccountAsync()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bohemiaId = new GameId(Guid.NewGuid());
        var session = await mediator.Send(new CreateSessionCommand(bohemiaId));
        _createdUsernames.Add(session.KeycloakUsername);
        return session.AccountId;
    }

    [Fact]
    public async Task Handle_ExistingAccount_ReturnsSubmitted()
    {
        var accountId = await CreateAccountAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "let me in"));

        Assert.True(result is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {result}");
    }

    [Fact]
    public async Task Handle_UnknownAccount_ReturnsAccountNotFound()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(new AccountId(Guid.NewGuid()), "text"));

        Assert.True(result is SubmitWhitelistApplicationResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_AlreadyPending_ReturnsAlreadyPending()
    {
        var accountId = await CreateAccountAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "first"));

        var result = await mediator.Send(new SubmitWhitelistApplicationCommand(accountId, "second"));

        Assert.True(result is SubmitWhitelistApplicationResult.AlreadyPending, $"Expected AlreadyPending, got {result}");
    }
}
