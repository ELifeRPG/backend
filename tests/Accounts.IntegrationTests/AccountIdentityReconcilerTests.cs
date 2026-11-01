using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// The reconciliation table, exercised directly against Postgres. No Keycloak needed: the binding
/// Keycloak would assert is simply passed in, which is also what lets these cover the refusals —
/// they are unreachable through a live Keycloak without contriving two accounts first.
///
/// Requires the local infra stack (`docker compose up -d`), like the other integration suites.
/// </summary>
public sealed class AccountIdentityReconcilerTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Reconcile_WithNothingKnown_CreatesAnAccountAndLinksTheIdentity()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var resolved = await ReconcileAsync(subject, [gameIdentityId]);

        Assert.True(resolved.Created);
        Assert.Equal(resolved.AccountId, await AccountIdForAsync(gameIdentityId));
    }

    /// <summary>
    /// The second-platform path, and the whole point of 1:n: a player who already has an account
    /// joins from another platform and that identity lands on the same account.
    /// </summary>
    [Fact]
    public async Task Reconcile_WithASecondIdentityForAKnownSubject_LinksItToTheSameAccount()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var pc = new GameIdentityId(Guid.NewGuid());
        var xbox = new GameIdentityId(Guid.NewGuid());

        var first = await ReconcileAsync(subject, [pc]);
        var second = await ReconcileAsync(subject, [xbox]);

        Assert.False(second.Created);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.AccountId, await AccountIdForAsync(xbox));
    }

    [Fact]
    public async Task Reconcile_ForAJoinFirstAccount_AttachesTheWebIdentity()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        var accountId = await CreateJoinFirstAccountAsync(gameIdentityId);
        var subject = new KeycloakUserId(Guid.NewGuid());

        var resolved = await ReconcileAsync(subject, [gameIdentityId]);

        Assert.False(resolved.Created);
        Assert.Equal(accountId, resolved.AccountId);

        using var scope = _provider.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<IAccountRepository>()
            .FindByIdAsync(accountId, CancellationToken.None);
        Assert.Equal(subject, account!.KeycloakUserId);
    }

    [Fact]
    public async Task Reconcile_Repeatedly_IsIdempotent()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var first = await ReconcileAsync(subject, [gameIdentityId]);
        var second = await ReconcileAsync(subject, [gameIdentityId]);

        Assert.Equal(first.AccountId, second.AccountId);
        Assert.False(second.Created);
    }

    /// <summary>
    /// Re-pointing an identity at another web account would hand over every character behind it.
    /// </summary>
    [Fact]
    public async Task Reconcile_ForAnIdentityOwnedByAnotherSubject_Refuses()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        await ReconcileAsync(new KeycloakUserId(Guid.NewGuid()), [gameIdentityId]);

        var result = await ReconcileRawAsync(new KeycloakUserId(Guid.NewGuid()), [gameIdentityId]);

        Assert.True(result is AccountIdentityReconciliationResult.Conflict, $"Expected Conflict, got {result}.");
    }

    /// <summary>
    /// The routine absorb: the player signed up on the portal, then joined before linking, so their
    /// join created a second account. Linking reunites them — and the approved whitelist
    /// application has to come along, or they land in not_whitelisted on the next join.
    /// </summary>
    [Fact]
    public async Task Reconcile_WithAPortalAccountAndAJoinFirstAccount_AbsorbsAndCarriesTheApprovedApplication()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var portalAccountId = await CreatePortalAccountAsync(subject);
        await ApproveWhitelistApplicationAsync(portalAccountId);
        var joinFirstAccountId = await CreateJoinFirstAccountAsync(gameIdentityId);

        var resolved = await ReconcileAsync(subject, [gameIdentityId]);

        // The survivor is the account a gameserver has already seen.
        Assert.Equal(joinFirstAccountId, resolved.AccountId);

        using var scope = _provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();

        var loser = await accounts.FindByIdAsync(portalAccountId, CancellationToken.None);
        Assert.Equal(joinFirstAccountId, loser!.AbsorbedInto);
        Assert.Null(loser.KeycloakUserId);

        // Single-valued, and crucially without throwing: both rows briefly held one subject.
        var bySubject = await accounts.FindByKeycloakUserIdAsync(subject, CancellationToken.None);
        Assert.Equal(joinFirstAccountId, bySubject!.Id);

        var approved = await scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>()
            .FindApprovedAsync(joinFirstAccountId, CancellationToken.None);
        Assert.NotNull(approved);
    }

    /// <summary>
    /// Both sides have been handed to gameservers, so merging could discard characters on one of
    /// them. This module cannot see Characters/Banking/Shops state, so it refuses rather than guesses.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenBothAccountsHoldIdentities_Refuses()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var alreadyLinked = new GameIdentityId(Guid.NewGuid());
        await ReconcileAsync(subject, [alreadyLinked]);

        var otherIdentity = new GameIdentityId(Guid.NewGuid());
        await CreateJoinFirstAccountAsync(otherIdentity);

        var result = await ReconcileRawAsync(subject, [otherIdentity]);

        Assert.True(result is AccountIdentityReconciliationResult.Conflict, $"Expected Conflict, got {result}.");
    }

    [Fact]
    public async Task Reconcile_WithIdentitiesOnTwoDifferentAccounts_Refuses()
    {
        var first = new GameIdentityId(Guid.NewGuid());
        var second = new GameIdentityId(Guid.NewGuid());
        await CreateJoinFirstAccountAsync(first);
        await CreateJoinFirstAccountAsync(second);

        var result = await ReconcileRawAsync(new KeycloakUserId(Guid.NewGuid()), [first, second]);

        Assert.True(result is AccountIdentityReconciliationResult.Conflict, $"Expected Conflict, got {result}.");
    }

    /// <summary>
    /// Ban laundering: absorbing a locked account would move its subject onto an Active survivor and
    /// drop the moderation state entirely.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenTheWebAccountIsLocked_Refuses()
    {
        var subject = new KeycloakUserId(Guid.NewGuid());
        var portalAccountId = await CreatePortalAccountAsync(subject);
        await LockAsync(portalAccountId);

        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        await CreateJoinFirstAccountAsync(gameIdentityId);

        var result = await ReconcileRawAsync(subject, [gameIdentityId]);

        Assert.True(result is AccountIdentityReconciliationResult.Conflict, $"Expected Conflict, got {result}.");
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<AccountIdentityReconciliationResult> ReconcileRawAsync(
        KeycloakUserId subject, IReadOnlyCollection<GameIdentityId> gameIdentityIds)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountIdentityReconciler>()
            .ReconcileAsync(subject, gameIdentityIds, platform: null, CancellationToken.None);
    }

    private async Task<AccountIdentityReconciliationResult.Resolved> ReconcileAsync(
        KeycloakUserId subject, IReadOnlyCollection<GameIdentityId> gameIdentityIds)
    {
        var result = await ReconcileRawAsync(subject, gameIdentityIds);
        if (result is not AccountIdentityReconciliationResult.Resolved resolved)
        {
            throw new InvalidOperationException($"Expected Resolved, got {result}.");
        }

        return resolved;
    }

    private async Task<AccountId?> AccountIdForAsync(GameIdentityId gameIdentityId)
    {
        using var scope = _provider.CreateScope();
        var link = await scope.ServiceProvider.GetRequiredService<IGameAccountLinkRepository>()
            .FindByGameIdentityIdAsync(gameIdentityId, CancellationToken.None);
        return link?.AccountId;
    }

    private async Task<AccountId> CreateJoinFirstAccountAsync(GameIdentityId gameIdentityId)
    {
        using var scope = _provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var links = scope.ServiceProvider.GetRequiredService<IGameAccountLinkRepository>();

        var accountId = new AccountId(Guid.NewGuid());
        var created = new AccountCreated(accountId, KeycloakUserId: null);
        accounts.StartStream(Account.Create(created), created);
        accounts.Append(accountId, new GameAccountLinked(accountId, gameIdentityId, Platform: null));
        links.Insert(GameAccountLink.Create(gameIdentityId, accountId, platform: null, DateTimeOffset.UtcNow));
        await accounts.SaveChangesAsync(CancellationToken.None);

        return accountId;
    }

    private async Task<AccountId> CreatePortalAccountAsync(KeycloakUserId subject)
    {
        using var scope = _provider.CreateScope();
        return (await TestAccounts.CreateAsync(scope.ServiceProvider, keycloakUserId: subject)).Id;
    }

    private async Task ApproveWhitelistApplicationAsync(AccountId accountId)
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();

        var id = new WhitelistApplicationId(Guid.NewGuid());
        var submitted = new WhitelistApplicationSubmitted(id, accountId, "please");
        var application = WhitelistApplication.Create(submitted);
        repository.StartStream(application, submitted);
        repository.Append(id, application.StartReview()!);
        repository.Append(id, application.Approve()!);
        await repository.SaveChangesAsync(CancellationToken.None);
    }

    private async Task LockAsync(AccountId accountId)
    {
        using var scope = _provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByIdAsync(accountId, CancellationToken.None);
        accounts.Append(accountId, account!.Lock());
        await accounts.SaveChangesAsync(CancellationToken.None);
    }
}
