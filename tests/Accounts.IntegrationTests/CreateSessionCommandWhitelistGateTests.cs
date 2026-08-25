using ELifeRPG.Accounts.Application.Hive;
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
[Collection("HiveSettings")]
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
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(SessionStatus.Active, result.Status);
    }

    [Fact]
    public async Task Handle_WhitelistOnNoApplication_ReturnsNotWhitelisted()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: true), CancellationToken.None);
        try
        {
            var bohemiaId = new GameId(Guid.NewGuid());

            var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

            _createdUsernames.Add(result.KeycloakUsername);
            Assert.Equal(SessionStatus.NotWhitelisted, result.Status);
        }
        finally
        {
            await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: false), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Handle_WhitelistOnWithApprovedApplication_ReturnsActive()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: true), CancellationToken.None);
        try
        {
            var bohemiaId = new GameId(Guid.NewGuid());

            var first = await mediator.Send(new CreateSessionCommand(bohemiaId));
            _createdUsernames.Add(first.KeycloakUsername);
            var submitResult = await mediator.Send(new SubmitWhitelistApplicationCommand(first.AccountId, "text"));
            Assert.True(submitResult is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {submitResult}");
            if (submitResult is not SubmitWhitelistApplicationResult.Submitted submitted)
            {
                throw new InvalidOperationException("Unreachable.");
            }

            await mediator.Send(new StartWhitelistApplicationReviewCommand(submitted.WhitelistApplicationId));
            await mediator.Send(new ApproveWhitelistApplicationCommand(submitted.WhitelistApplicationId));

            var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

            Assert.Equal(SessionStatus.Active, result.Status);
        }
        finally
        {
            await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: false), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bootstrap_WithHiveWhitelistEnabled_ApprovedOnceStaysActiveOnRebootstrap()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: true), CancellationToken.None);
        try
        {
            var bohemiaId = new GameId(Guid.NewGuid());
            var bootstrap = await mediator.Send(
                new CreateSessionCommand(bohemiaId), CancellationToken.None);
            _createdUsernames.Add(bootstrap.KeycloakUsername);
            Assert.Equal(SessionStatus.NotWhitelisted, bootstrap.Status);

            var submitResult = await mediator.Send(
                new SubmitWhitelistApplicationCommand(bootstrap.AccountId, "please"), CancellationToken.None);
            Assert.True(submitResult is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {submitResult}");
            if (submitResult is not SubmitWhitelistApplicationResult.Submitted submitted)
            {
                throw new InvalidOperationException("Unreachable.");
            }

            await mediator.Send(new StartWhitelistApplicationReviewCommand(submitted.WhitelistApplicationId), CancellationToken.None);
            await mediator.Send(new ApproveWhitelistApplicationCommand(submitted.WhitelistApplicationId), CancellationToken.None);

            // Approved once — subsequent bootstraps for the same account stay whitelisted hive-wide.
            var rebootstrap = await mediator.Send(
                new CreateSessionCommand(bohemiaId), CancellationToken.None);

            Assert.Equal(SessionStatus.Active, rebootstrap.Status);
        }
        finally
        {
            await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: false), CancellationToken.None);
        }
    }
}
