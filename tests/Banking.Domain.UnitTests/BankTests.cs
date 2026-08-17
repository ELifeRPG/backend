using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using Xunit;

namespace ELifeRPG.Banking.Domain.UnitTests;

public class BankTests
{
    [Fact]
    public void Create_SetsPropertiesFromEvent()
    {
        var bankId = new BankId(Guid.NewGuid());
        var domainEvent = new BankOpened(bankId, "First National", 0.20m, 0.02m);

        var bank = Bank.Create(domainEvent);

        Assert.Equal(bankId, bank.Id);
        Assert.Equal("First National", bank.Name);
        Assert.Equal(0.20m, bank.TransactionFeeBase);
        Assert.Equal(0.02m, bank.TransactionFeeMultiplier);
    }
}
