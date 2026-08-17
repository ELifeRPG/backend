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
}
