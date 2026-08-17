using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Banking.Domain.UnitTests;

public class BankAccountTests
{
    private static readonly CharacterId Owner = new(Guid.NewGuid());
    private static readonly CompanyId OwnerCompany = new(Guid.NewGuid());

    private static BankAccount CreatePersonalAccount(decimal feeBase = 0.20m, decimal feeMultiplier = 0.02m)
    {
        var domainEvent = new BankAccountOpened(
            new BankAccountId(Guid.NewGuid()),
            new BankId(Guid.NewGuid()),
            BankAccountType.Personal,
            Owner,
            null,
            "EL12345",
            feeBase,
            feeMultiplier);

        return BankAccount.Create(domainEvent);
    }

    private static BankAccount CreateCorporateAccount(decimal feeBase = 0.20m, decimal feeMultiplier = 0.02m)
    {
        var domainEvent = new BankAccountOpened(
            new BankAccountId(Guid.NewGuid()),
            new BankId(Guid.NewGuid()),
            BankAccountType.Corporate,
            null,
            OwnerCompany,
            "EL54321",
            feeBase,
            feeMultiplier);

        return BankAccount.Create(domainEvent);
    }

    [Fact]
    public void Create_Personal_SetsPropertiesFromEvent()
    {
        var account = CreatePersonalAccount();

        Assert.Equal(BankAccountType.Personal, account.Type);
        Assert.Equal(Owner, account.OwnerCharacterId);
        Assert.Null(account.OwnerCompanyId);
        Assert.Equal("EL12345", account.Number);
        Assert.Equal(0m, account.Balance);
    }

    [Fact]
    public void Create_Corporate_SetsPropertiesFromEvent()
    {
        var account = CreateCorporateAccount();

        Assert.Equal(BankAccountType.Corporate, account.Type);
        Assert.Equal(OwnerCompany, account.OwnerCompanyId);
        Assert.Null(account.OwnerCharacterId);
    }

    [Fact]
    public void Deposit_AddsAmountMinusFee()
    {
        var account = CreatePersonalAccount();

        var domainEvent = account.Deposit(100m);

        Assert.Equal(2.2m, domainEvent.Fee);
        Assert.Equal(97.8m, account.Balance);
    }

    [Fact]
    public void Withdraw_WhenAuthorized_SubtractsAmountPlusFee()
    {
        var account = CreatePersonalAccount();
        account.Deposit(100m);

        var domainEvent = account.Withdraw(Owner, isAuthorized: true, 50m);

        Assert.Equal(Owner, domainEvent.CharacterId);
        Assert.Equal(1.2m, domainEvent.Fee);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void Withdraw_WhenNotAuthorized_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(100m);
        var otherCharacter = new CharacterId(Guid.NewGuid());

        Assert.Throws<BankAccountAuthorizationException>(() => account.Withdraw(otherCharacter, isAuthorized: false, 10m));
    }

    [Fact]
    public void Withdraw_OnCorporateAccount_WhenAuthorized_Succeeds()
    {
        var account = CreateCorporateAccount();
        account.Deposit(100m);
        var actingCharacter = new CharacterId(Guid.NewGuid());

        var domainEvent = account.Withdraw(actingCharacter, isAuthorized: true, 50m);

        Assert.Equal(actingCharacter, domainEvent.CharacterId);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void Withdraw_InsufficientBalance_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(10m);

        Assert.Throws<InsufficientBalanceException>(() => account.Withdraw(Owner, isAuthorized: true, 1000m));
    }

    [Fact]
    public void TransferOut_WhenAuthorized_SubtractsAmountPlusFee()
    {
        var account = CreatePersonalAccount();
        account.Deposit(100m);
        var targetId = new BankAccountId(Guid.NewGuid());

        var domainEvent = account.TransferOut(Owner, isAuthorized: true, targetId, 50m);

        Assert.Equal(targetId, domainEvent.TargetBankAccountId);
        Assert.Equal(1.2m, domainEvent.Fee);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void TransferOut_WhenNotAuthorized_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(100m);
        var otherCharacter = new CharacterId(Guid.NewGuid());

        Assert.Throws<BankAccountAuthorizationException>(
            () => account.TransferOut(otherCharacter, isAuthorized: false, new BankAccountId(Guid.NewGuid()), 10m));
    }

    [Fact]
    public void TransferOut_InsufficientBalance_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(10m);

        Assert.Throws<InsufficientBalanceException>(
            () => account.TransferOut(Owner, isAuthorized: true, new BankAccountId(Guid.NewGuid()), 1000m));
    }

    [Fact]
    public void TransferOut_ToSameAccount_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(100m);

        Assert.Throws<InvalidOperationException>(() => account.TransferOut(Owner, isAuthorized: true, account.Id, 10m));
    }

    [Fact]
    public void ReceiveTransfer_AddsAmount_NoFee()
    {
        var account = CreatePersonalAccount();
        var sourceId = new BankAccountId(Guid.NewGuid());

        var domainEvent = account.ReceiveTransfer(sourceId, 25m);

        Assert.Equal(sourceId, domainEvent.SourceBankAccountId);
        Assert.Equal(25m, account.Balance);
    }
}
