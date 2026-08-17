using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed partial class BankAccountProjection : SingleStreamProjection<BankAccount, BankAccountId>
{
    public static BankAccount Create(BankAccountOpened domainEvent) => BankAccount.Create(domainEvent);

    public void Apply(BankAccount bankAccount, BankAccountDeposited domainEvent) => bankAccount.Apply(domainEvent);

    public void Apply(BankAccount bankAccount, BankAccountWithdrawn domainEvent) => bankAccount.Apply(domainEvent);

    public void Apply(BankAccount bankAccount, BankAccountTransferredOut domainEvent) => bankAccount.Apply(domainEvent);

    public void Apply(BankAccount bankAccount, BankAccountTransferredIn domainEvent) => bankAccount.Apply(domainEvent);
}
