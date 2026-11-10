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

    // Lock/unlock disables and re-enables the account's Keycloak user, so this needs a real one.
    // Linked as well, because the assertions re-bootstrap a session for the same Game Identity ID.
    private async Task<(AccountId AccountId, GameIdentityId GameIdentityId, string KeycloakUsername)> CreateAccountAsync()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        var username = $"test-lock-{Guid.NewGuid():N}";
        var keycloakUserId = new KeycloakUserId(await _keycloak.CreateUserAsync(username));
        _createdUsernames.Add(username);

        using var scope = _provider.CreateScope();
        var account = await TestAccounts.CreateAsync(scope.ServiceProvider, gameIdentityId, keycloakUserId);
        return (account.Id, gameIdentityId, username);
    }

    [Fact]
    public async Task Handle_ActiveAccount_LocksItAndDisablesTheKeycloakUser()
    {
        var (accountId, gameIdentityId, username) = await CreateAccountAsync();

        var result = await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.True(result is LockAccountResult.Locked, $"Expected Locked, got {result}");
        Assert.False(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterLock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(gameIdentityId));
        Assert.Equal(SessionStatus.Blocked, sessionAfterLock.Status);
    }

    [Fact]
    public async Task Handle_LockedAccount_TokenExchangeAgainstItsKeycloakUserIsRefused()
    {
        // Keycloak 26.0.8 still minted an impersonation token (requested_subject) for a disabled user,
        // and this test used to pin that limitation so an upgrade that closed it would get noticed.
        // Keycloak 26.8.1 closed it: the exchange is refused with access_denied (ARCHITECTURE.md §4.3).
        // Now it guards that behavior. The application-layer AccountStatus check in
        // BridgeTokenProvider stays the primary enforcement either way.
        var (accountId, _, username) = await CreateAccountAsync();

        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        Assert.False(await _keycloak.TokenExchangeSucceedsAsync(username));
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
        var (accountId, gameIdentityId, username) = await CreateAccountAsync();
        await Send<LockAccountCommand, LockAccountResult>(new LockAccountCommand(accountId));

        var result = await Send<UnlockAccountCommand, UnlockAccountResult>(new UnlockAccountCommand(accountId));

        Assert.True(result is UnlockAccountResult.Unlocked, $"Expected Unlocked, got {result}");
        Assert.True(await _keycloak.GetUserEnabledAsync(username));

        var sessionAfterUnlock = await Send<CreateSessionCommand, CreateSessionResponse>(new CreateSessionCommand(gameIdentityId));
        Assert.Equal(SessionStatus.Active, sessionAfterUnlock.Status);
    }

    private async Task<TResponse> Send<TCommand, TResponse>(TCommand command) where TCommand : IRequest<TResponse>
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
