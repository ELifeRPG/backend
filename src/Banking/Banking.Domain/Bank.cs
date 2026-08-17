using ELifeRPG.Banking.Domain.Events;

namespace ELifeRPG.Banking.Domain;

public class Bank
{
    public BankId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal TransactionFeeBase { get; private set; }

    public decimal TransactionFeeMultiplier { get; private set; }

    public static Bank Create(BankOpened domainEvent)
    {
        var bank = new Bank();
        bank.Apply(domainEvent);
        return bank;
    }

    public void Apply(BankOpened domainEvent)
    {
        Id = domainEvent.Id;
        Name = domainEvent.Name;
        TransactionFeeBase = domainEvent.TransactionFeeBase;
        TransactionFeeMultiplier = domainEvent.TransactionFeeMultiplier;
    }
}
