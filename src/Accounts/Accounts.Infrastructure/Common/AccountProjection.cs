using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class AccountProjection : SingleStreamProjection<Account, AccountId>
{
    public static Account Create(AccountCreated domainEvent) => Account.Create(domainEvent);

    public void Apply(Account account, AccountLocked domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountUnlocked domainEvent) => account.Apply(domainEvent);
}
