using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Banking.Infrastructure.Common;

public sealed partial class BankProjection : SingleStreamProjection<Bank, BankId>
{
    public static Bank Create(BankOpened domainEvent) => Bank.Create(domainEvent);
}
