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
///
/// Submission is now player-facing: the account comes from the caller's Keycloak subject rather
/// than from the request body, so every case here has to say who is calling.
/// </summary>
public sealed class SubmitWhitelistApplicationCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>
    /// Deliberately unlinked: applying for the whitelist from the portal before ever joining the
    /// gameserver is the flow this whole design exists to support.
    /// </summary>
    [Fact]
    public async Task Handle_UnlinkedAccountApplyingForItself_ReturnsSubmitted()
    {
        var account = await CreateAccountAsync();

        var result = await SendAs(account.KeycloakUserId, new SubmitWhitelistApplicationCommand("let me in"));

        Assert.True(result is SubmitWhitelistApplicationResult.Submitted, $"Expected Submitted, got {result}");
    }

    [Fact]
    public async Task Handle_CallerWithNoAccount_ReturnsAccountNotFound()
    {
        var result = await SendAs(new KeycloakUserId(Guid.NewGuid()), new SubmitWhitelistApplicationCommand("text"));

        Assert.True(result is SubmitWhitelistApplicationResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_AnonymousCaller_ReturnsAccountNotFound()
    {
        var result = await SendAs(null, new SubmitWhitelistApplicationCommand("text"));

        Assert.True(result is SubmitWhitelistApplicationResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_AlreadyPending_ReturnsAlreadyPending()
    {
        var account = await CreateAccountAsync();
        await SendAs(account.KeycloakUserId, new SubmitWhitelistApplicationCommand("first"));

        var result = await SendAs(account.KeycloakUserId, new SubmitWhitelistApplicationCommand("second"));

        Assert.True(result is SubmitWhitelistApplicationResult.AlreadyPending, $"Expected AlreadyPending, got {result}");
    }

    private async Task<TestAccount> CreateAccountAsync()
    {
        using var scope = _provider.CreateScope();
        return await TestAccounts.CreateAsync(scope.ServiceProvider);
    }

    private async Task<SubmitWhitelistApplicationResult> SendAs(KeycloakUserId? caller, SubmitWhitelistApplicationCommand command)
    {
        _provider.GetRequiredService<TestCurrentKeycloakUser>().Current = caller;
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
