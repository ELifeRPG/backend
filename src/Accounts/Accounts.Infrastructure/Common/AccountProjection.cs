using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class AccountProjection : SingleStreamProjection<Account, AccountId>
{
    public static Account Create(AccountCreated domainEvent) => Account.Create(domainEvent);

    // This projection is inline and silently ignores events it has no Apply for — a missing
    // handler here does not fail, it just leaves the document stale. BohemiaIdBound needs one or
    // the account would still read as unlinked after the player linked.
    public void Apply(Account account, BohemiaIdBound domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountLocked domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountUnlocked domainEvent) => account.Apply(domainEvent);
}
