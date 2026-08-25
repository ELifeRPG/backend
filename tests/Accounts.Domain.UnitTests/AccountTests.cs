using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public class AccountTests
{
    private static Account CreateAccount()
    {
        var domainEvent = new AccountCreated(new AccountId(Guid.NewGuid()), new GameId(Guid.NewGuid()), new KeycloakUserId(Guid.NewGuid()));
        return Account.Create(domainEvent);
    }

    [Fact]
    public void Create_SetsPropertiesFromEvent()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var bohemiaId = new GameId(Guid.NewGuid());
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());
        var domainEvent = new AccountCreated(accountId, bohemiaId, keycloakUserId);

        var account = Account.Create(domainEvent);

        Assert.Equal(accountId, account.Id);
        Assert.Equal(bohemiaId, account.BohemiaId);
        Assert.Equal(keycloakUserId, account.KeycloakUserId);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Lock_WhenActive_SetsStatusToLocked()
    {
        var account = CreateAccount();

        account.Lock();

        Assert.Equal(AccountStatus.Locked, account.Status);
    }

    [Fact]
    public void Lock_WhenActive_ReturnsEventForThisAccount()
    {
        var account = CreateAccount();

        var domainEvent = account.Lock();

        Assert.Equal(account.Id, domainEvent.Id);
    }

    [Fact]
    public void Lock_WhenAlreadyLocked_Throws()
    {
        var account = CreateAccount();
        account.Lock();

        Assert.Throws<AccountStatusException>(() => account.Lock());
    }

    [Fact]
    public void Unlock_WhenLocked_SetsStatusToActive()
    {
        var account = CreateAccount();
        account.Lock();

        account.Unlock();

        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Unlock_WhenAlreadyActive_Throws()
    {
        var account = CreateAccount();

        Assert.Throws<AccountStatusException>(() => account.Unlock());
    }

    [Fact]
    public void Apply_ReplayingCreatedThenLocked_ResultsInLockedAccount()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var bohemiaId = new GameId(Guid.NewGuid());
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, bohemiaId, keycloakUserId));
        account.Apply(new AccountLocked(accountId));

        Assert.Equal(AccountStatus.Locked, account.Status);
    }

    // --- portal-first creation and later binding ---------------------------------------

    [Fact]
    public void Create_WithoutABohemiaId_IsUnlinked()
    {
        var domainEvent = new AccountCreated(new AccountId(Guid.NewGuid()), null, new KeycloakUserId(Guid.NewGuid()));

        var account = Account.Create(domainEvent);

        Assert.Null(account.BohemiaId);
        Assert.False(account.IsLinked);
        // Being unlinked is the normal state of a fresh web signup, not a degraded one.
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void BindBohemiaId_WhenUnlinked_LinksTheAccount()
    {
        var account = CreateUnlinkedAccount();
        var bohemiaId = new GameId(Guid.NewGuid());

        var domainEvent = account.BindBohemiaId(bohemiaId);

        Assert.Equal(bohemiaId, account.BohemiaId);
        Assert.True(account.IsLinked);
        Assert.Equal(account.Id, domainEvent.Id);
        Assert.Equal(bohemiaId, domainEvent.BohemiaId);
    }

    /// <summary>
    /// Rebinding would silently reassign every character and balance behind the account, so it is
    /// refused rather than treated as an idempotent no-op.
    /// </summary>
    [Fact]
    public void BindBohemiaId_WhenAlreadyLinkedToAnother_Throws()
    {
        var account = CreateUnlinkedAccount();
        account.BindBohemiaId(new GameId(Guid.NewGuid()));

        Assert.Throws<AccountStatusException>(() => account.BindBohemiaId(new GameId(Guid.NewGuid())));
    }

    [Fact]
    public void BindBohemiaId_WhenAlreadyLinkedToTheSameId_Throws()
    {
        var account = CreateUnlinkedAccount();
        var bohemiaId = new GameId(Guid.NewGuid());
        account.BindBohemiaId(bohemiaId);

        Assert.Throws<AccountStatusException>(() => account.BindBohemiaId(bohemiaId));
    }

    /// <summary>
    /// The projection is inline and replays from the event stream, so the binding has to survive a
    /// replay — a BohemiaIdBound with no Apply would leave the account reading as unlinked forever.
    /// </summary>
    [Fact]
    public void Apply_ReplayingCreatedUnlinkedThenBound_ResultsInALinkedAccount()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var bohemiaId = new GameId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, null, new KeycloakUserId(Guid.NewGuid())));
        account.Apply(new BohemiaIdBound(accountId, bohemiaId));

        Assert.Equal(bohemiaId, account.BohemiaId);
        Assert.True(account.IsLinked);
    }

    private static Account CreateUnlinkedAccount()
        => Account.Create(new AccountCreated(new AccountId(Guid.NewGuid()), null, new KeycloakUserId(Guid.NewGuid())));
}
