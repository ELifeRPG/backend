using ELifeRPG.Accounts.Application.Common;
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
public sealed class CreateSessionCommandWhitelistGateTests : IAsyncLifetime
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

    [Fact]
    public async Task Handle_WhitelistOffForServer_ReturnsActiveRegardlessOfApplications()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverClientId = $"whitelist-off-{Guid.NewGuid()}";
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(SessionStatus.Active, result.Status);
    }

    [Fact]
    public async Task Handle_WhitelistOnNoApplication_ReturnsNotWhitelisted()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var gameServerRepository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var serverClientId = $"whitelist-on-{Guid.NewGuid()}";
        await gameServerRepository.UpsertAsync(new GameServer { ClientId = serverClientId, WhitelistEnabled = true }, CancellationToken.None);
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(SessionStatus.NotWhitelisted, result.Status);
    }

    [Fact]
    public async Task Handle_WhitelistOnWithApprovedApplication_ReturnsActive()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var gameServerRepository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var serverClientId = $"whitelist-approved-{Guid.NewGuid()}";
        await gameServerRepository.UpsertAsync(new GameServer { ClientId = serverClientId, WhitelistEnabled = true }, CancellationToken.None);
        var bohemiaId = new GameId(Guid.NewGuid());

        var first = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));
        _createdUsernames.Add(first.KeycloakUsername);
        var submitResult = await mediator.Send(new SubmitWhitelistApplicationCommand(first.AccountId, serverClientId, "text"));
        Assert.True(submitResult is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {submitResult}");
        if (submitResult is not SubmitWhitelistApplicationResult.Submitted submitted)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        await mediator.Send(new StartWhitelistApplicationReviewCommand(submitted.WhitelistApplicationId));
        await mediator.Send(new ApproveWhitelistApplicationCommand(submitted.WhitelistApplicationId));

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, serverClientId));

        Assert.Equal(SessionStatus.Active, result.Status);
    }
}
