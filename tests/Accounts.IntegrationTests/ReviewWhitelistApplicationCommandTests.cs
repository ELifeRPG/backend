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
public sealed class ReviewWhitelistApplicationCommandTests : IAsyncLifetime
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

    private async Task<WhitelistApplicationId> SubmitApplicationAsync()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var bohemiaId = new GameId(Guid.NewGuid());
        var session = await mediator.Send(new CreateSessionCommand(bohemiaId));
        _createdUsernames.Add(session.KeycloakUsername);
        var submitResult = await mediator.Send(new SubmitWhitelistApplicationCommand(session.AccountId, "text"));
        Assert.True(submitResult is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {submitResult}");
        if (submitResult is not SubmitWhitelistApplicationResult.Submitted submitted)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return submitted.WhitelistApplicationId;
    }

    [Fact]
    public async Task ApproveWithoutStartingReview_ReturnsInvalidState()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.True(result is ApproveWhitelistApplicationResult.InvalidState, $"Expected InvalidState, got {result}");
    }

    [Fact]
    public async Task StartReviewThenApprove_ReturnsApproved()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new StartWhitelistApplicationReviewCommand(id));

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.True(result is ApproveWhitelistApplicationResult.Approved, $"Expected Approved, got {result}");
    }

    [Fact]
    public async Task ApproveTwice_SecondCallIsIdempotent()
    {
        var id = await SubmitApplicationAsync();
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new StartWhitelistApplicationReviewCommand(id));
        await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(id));

        Assert.True(result is ApproveWhitelistApplicationResult.Approved, $"Expected Approved, got {result}");
    }

    [Fact]
    public async Task ApproveUnknownId_ReturnsNotFound()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApproveWhitelistApplicationCommand(new WhitelistApplicationId(Guid.NewGuid())));

        Assert.True(result is ApproveWhitelistApplicationResult.NotFound, $"Expected NotFound, got {result}");
    }
}
