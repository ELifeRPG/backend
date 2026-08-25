using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

public class Account
{
    public AccountId Id { get; private set; }

    /// <summary>
    /// Null until the player links their in-game identity. An account is created by ordinary web
    /// signup — which happens before the player ever joins the gameserver — so being unlinked is
    /// the normal starting state, not an error.
    /// </summary>
    public GameId? BohemiaId { get; private set; }

    public KeycloakUserId KeycloakUserId { get; private set; }

    public AccountStatus Status { get; private set; } = AccountStatus.Active;

    public bool IsLinked => BohemiaId is not null;

    public static Account Create(AccountCreated domainEvent)
    {
        var account = new Account();
        account.Apply(domainEvent);
        return account;
    }

    /// <summary>
    /// Records that this account now owns <paramref name="bohemiaId"/>. Rebinding to a different
    /// Bohemia ID is refused: Keycloak enforces the same uniqueness on its side, and letting an
    /// account swap game identities would silently reassign every character and balance behind it.
    /// </summary>
    public BohemiaIdBound BindBohemiaId(GameId bohemiaId)
    {
        if (BohemiaId is { } existing)
        {
            if (existing.Value == bohemiaId.Value)
            {
                throw new AccountStatusException("Account is already linked to this Bohemia ID.");
            }

            throw new AccountStatusException("Account is already linked to a different Bohemia ID.");
        }

        var domainEvent = new BohemiaIdBound(Id, bohemiaId);
        Apply(domainEvent);
        return domainEvent;
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

    public void Apply(BohemiaIdBound domainEvent) => BohemiaId = domainEvent.BohemiaId;

    public void Apply(AccountLocked domainEvent) => Status = AccountStatus.Locked;

    public void Apply(AccountUnlocked domainEvent) => Status = AccountStatus.Active;
}
