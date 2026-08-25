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
/// Joins the "HiveSettings" collection (see <see cref="HiveSettingsCollection"/>) because this class
/// asserts <see cref="SessionStatus.Active"/> unconditionally after unlocking — it would flake if it
/// interleaved with <see cref="CreateSessionCommandWhitelistGateTests"/> or
/// <see cref="HiveSettingsTests"/> toggling the shared <c>WhitelistEnabled</c> singleton mid-run.
/// </summary>
[Collection("HiveSettings")]
public sealed class LockAccountCommandTests : IAsyncLifetime
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

    private async Task<(AccountId AccountId, GameId BohemiaId, string KeycloakUsername)> CreateAccountAsync()
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var response = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        _createdUsernames.Add(response.KeycloakUsername);
        return (response.AccountId, bohemiaId, response.KeycloakUsername);
    }

    [Fact]
    public async Task Handle_ActiveAccount_LocksItAndDisablesTheKeycloakUser()
    {
        var (accountId, bohemiaId, username) = await CreateAccountAsync();

        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(result is LockAccountResult.Locked, $"Expected Locked, got {result}");
        Assert.False(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterLock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        Assert.Equal(SessionStatus.Blocked, sessionAfterLock.Status);
    }

    [Fact]
    public async Task Handle_LockedAccount_TokenExchangeAgainstItsKeycloakUserStillSucceeds_KnownKeycloakLimitation()
    {
        // Documents a verified Keycloak behavior, not the desired one: disabling a user does NOT stop
        // Keycloak's classic impersonation-based token-exchange grant (requested_subject) from minting it
        // a valid token — unlike normal login grants, which do honor `enabled`. See the "Post-implementation
        // correction" section of docs/superpowers/specs/2026-08-14-account-blocking-login-flow-design.md and
        // ARCHITECTURE.md §4.3 for the full investigation. The real enforcement boundary is application-layer:
        // player-connected checks AccountStatus before ever attempting this exchange (see
        // SessionLocalEndpoints.cs). This test exists so a future Keycloak upgrade or config change that
        // starts enforcing `enabled` here gets noticed (it'll start failing) rather than silently assumed.
        var (accountId, _, username) = await CreateAccountAsync();

        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(await _keycloak.TokenExchangeSucceedsAsync(username));
    }

    [Fact]
    public async Task Handle_AlreadyLockedAccount_StaysLockedAndDoesNotThrow()
    {
        var (accountId, _, username) = await CreateAccountAsync();
        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(result is LockAccountResult.Locked, $"Expected Locked, got {result}");
        Assert.False(await _keycloak.GetUserEnabledAsync(username));
    }

    [Fact]
    public async Task Handle_UnknownAccount_ReturnsAccountNotFound()
    {
        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(new AccountId(Guid.NewGuid())));

        Assert.True(result is LockAccountResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_LockedAccount_UnlockRestoresActiveAndReEnablesTheKeycloakUser()
    {
        var (accountId, bohemiaId, username) = await CreateAccountAsync();
        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        var result = await Send<UnlockAccountCommand, UnlockAccountResult>(new UnlockAccountCommand(accountId));

        Assert.True(result is UnlockAccountResult.Unlocked, $"Expected Unlocked, got {result}");
        Assert.True(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterUnlock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(bohemiaId));
        Assert.Equal(SessionStatus.Active, sessionAfterUnlock.Status);
    }

    private async Task<TResponse> Send<TCommand, TResponse>(TCommand command) where TCommand : IRequest<TResponse>
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
