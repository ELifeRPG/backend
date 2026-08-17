using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

public class Account
{
    public AccountId Id { get; private set; }

    public GameId BohemiaId { get; private set; }

    public KeycloakUserId KeycloakUserId { get; private set; }

    public AccountStatus Status { get; private set; } = AccountStatus.Active;

    public static Account Create(AccountCreated domainEvent)
    {
        var account = new Account();
        account.Apply(domainEvent);
        return account;
    }

    public AccountLocked Lock()
    {
        if (Status == AccountStatus.Locked)
        {
            throw new AccountStatusException("Account is already locked.");
        }

        var domainEvent = new AccountLocked(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public AccountUnlocked Unlock()
    {
        if (Status == AccountStatus.Active)
        {
            throw new AccountStatusException("Account is already active.");
        }

        var domainEvent = new AccountUnlocked(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(AccountCreated domainEvent)
    {
        Id = domainEvent.Id;
        BohemiaId = domainEvent.BohemiaId;
        KeycloakUserId = domainEvent.KeycloakUserId;
    }

    public void Apply(AccountLocked domainEvent) => Status = AccountStatus.Locked;

    public void Apply(AccountUnlocked domainEvent) => Status = AccountStatus.Active;
}
