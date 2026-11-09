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
    private static readonly Booking Booking = ELifeRPG.Banking.Domain.Booking.New(new GameServerId(Guid.NewGuid()));

    private static BankAccount CreatePersonalAccount(
        decimal feeBase = 0.20m, decimal feeMultiplier = 0.02m, BankId? bankId = null, CharacterId? owner = null)
    {
        var domainEvent = new BankAccountOpened(
            new BankAccountId(Guid.NewGuid()),
            bankId ?? new BankId(Guid.NewGuid()),
            BankAccountType.Personal,
            owner ?? Owner,
            null,
            "EL90FEBK0123456789",
            feeBase,
            feeMultiplier);

        return BankAccount.Create(domainEvent);
    }

    private static BankAccount CreateCorporateAccount(decimal feeBase = 0.20m, decimal feeMultiplier = 0.02m, BankId? bankId = null)
    {
        var domainEvent = new BankAccountOpened(
            new BankAccountId(Guid.NewGuid()),
            bankId ?? new BankId(Guid.NewGuid()),
            BankAccountType.Corporate,
            null,
            OwnerCompany,
            "EL27FEBK9876543210",
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
        Assert.Equal("EL90FEBK0123456789", account.Number);
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

        var domainEvent = account.Deposit(Booking, Owner, 100m);

        Assert.Equal(2.2m, domainEvent.Fee);
        Assert.Equal(97.8m, account.Balance);
    }

    [Fact]
    public void Deposit_RoundsTheFeeToWholeCentsHalfAwayFromZero()
    {
        // 0.20 + 0.25 * 0.02 = 0.205
        var account = CreatePersonalAccount();

        var domainEvent = account.Deposit(Booking, Owner, 0.25m);

        Assert.Equal(0.21m, domainEvent.Fee);
        Assert.Equal(0.04m, account.Balance);
    }

    [Fact]
    public void Withdraw_RoundsTheFeeAndKeepsTheBalanceInWholeCents()
    {
        // 0.20 + 33.33 * 0.02 = 0.8666
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);

        var domainEvent = account.Withdraw(Booking, Owner, isAuthorized: true, 33.33m);

        Assert.Equal(0.87m, domainEvent.Fee);
        Assert.Equal(97.8m - 33.33m - 0.87m, account.Balance);
        Assert.True(Money.IsWholeCents(account.Balance));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.005")]
    public void Deposit_WithInvalidAmount_Throws(string amount)
    {
        var account = CreatePersonalAccount();

        Assert.Throws<ArgumentOutOfRangeException>(() => account.Deposit(Booking, Owner, decimal.Parse(amount)));
    }

    [Fact]
    public void Withdraw_WithFractionalCentAmount_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);

        Assert.Throws<ArgumentOutOfRangeException>(() => account.Withdraw(Booking, Owner, isAuthorized: true, 1.005m));
    }

    [Fact]
    public void TransferOut_WithFractionalCentAmount_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => account.TransferOut(Booking, Owner, isAuthorized: true, CreatePersonalAccount(), 1.005m));
    }

    [Fact]
    public void Withdraw_WhenAuthorized_SubtractsAmountPlusFee()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);

        var domainEvent = account.Withdraw(Booking, Owner, isAuthorized: true, 50m);

        Assert.Equal(Owner, domainEvent.CharacterId);
        Assert.Equal(1.2m, domainEvent.Fee);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void Withdraw_WhenNotAuthorized_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);
        var otherCharacter = new CharacterId(Guid.NewGuid());

        Assert.Throws<BankAccountAuthorizationException>(() => account.Withdraw(Booking, otherCharacter, isAuthorized: false, 10m));
    }

    [Fact]
    public void Withdraw_OnCorporateAccount_WhenAuthorized_Succeeds()
    {
        var account = CreateCorporateAccount();
        account.Deposit(Booking, Owner, 100m);
        var actingCharacter = new CharacterId(Guid.NewGuid());

        var domainEvent = account.Withdraw(Booking, actingCharacter, isAuthorized: true, 50m);

        Assert.Equal(actingCharacter, domainEvent.CharacterId);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void Withdraw_InsufficientBalance_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 10m);

        Assert.Throws<InsufficientBalanceException>(() => account.Withdraw(Booking, Owner, isAuthorized: true, 1000m));
    }

    [Fact]
    public void TransferOut_WhenAuthorized_SubtractsAmountPlusFee()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);
        var target = CreatePersonalAccount();

        var domainEvent = account.TransferOut(Booking, Owner, isAuthorized: true, target, 50m);

        Assert.Equal(target.Id, domainEvent.TargetBankAccountId);
        Assert.Equal(1.2m, domainEvent.Fee);
        Assert.Equal(97.8m - 51.2m, account.Balance);
    }

    [Fact]
    public void TransferOut_BetweenOwnPersonalAccountsAtSameBank_IsFree()
    {
        var bankId = new BankId(Guid.NewGuid());
        var account = CreatePersonalAccount(bankId: bankId);
        account.Deposit(Booking, Owner, 100m);
        var balanceBefore = account.Balance;
        var target = CreatePersonalAccount(bankId: bankId);

        var domainEvent = account.TransferOut(Booking, Owner, isAuthorized: true, target, 50m);

        Assert.Equal(0m, domainEvent.Fee);
        Assert.Equal(balanceBefore - 50m, account.Balance);
    }

    [Fact]
    public void TransferOut_CanMoveTheWholeBalanceBetweenOwnAccountsAtSameBank()
    {
        var bankId = new BankId(Guid.NewGuid());
        var account = CreatePersonalAccount(bankId: bankId);
        account.Deposit(Booking, Owner, 100m);
        var target = CreatePersonalAccount(bankId: bankId);

        account.TransferOut(Booking, Owner, isAuthorized: true, target, account.Balance);

        Assert.Equal(0m, account.Balance);
    }

    [Fact]
    public void TransferOut_ToOwnPersonalAccountAtAnotherBank_PaysFee()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);
        var target = CreatePersonalAccount(owner: Owner);

        var domainEvent = account.TransferOut(Booking, Owner, isAuthorized: true, target, 50m);

        Assert.Equal(1.2m, domainEvent.Fee);
    }

    [Fact]
    public void TransferOut_ToAnotherCharactersAccountAtSameBank_PaysFee()
    {
        var bankId = new BankId(Guid.NewGuid());
        var account = CreatePersonalAccount(bankId: bankId);
        account.Deposit(Booking, Owner, 100m);
        var target = CreatePersonalAccount(bankId: bankId, owner: new CharacterId(Guid.NewGuid()));

        var domainEvent = account.TransferOut(Booking, Owner, isAuthorized: true, target, 50m);

        Assert.Equal(1.2m, domainEvent.Fee);
    }

    [Fact]
    public void TransferOut_BetweenPersonalAndCorporateAccountsAtSameBank_PaysFee()
    {
        var bankId = new BankId(Guid.NewGuid());
        var personal = CreatePersonalAccount(bankId: bankId);
        personal.Deposit(Booking, Owner, 100m);
        var corporate = CreateCorporateAccount(bankId: bankId);
        corporate.Deposit(Booking, Owner, 100m);

        Assert.Equal(1.2m, personal.TransferOut(Booking, Owner, isAuthorized: true, corporate, 50m).Fee);
        Assert.Equal(1.2m, corporate.TransferOut(Booking, Owner, isAuthorized: true, personal, 50m).Fee);
    }

    [Fact]
    public void TransferOut_WhenNotAuthorized_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);
        var otherCharacter = new CharacterId(Guid.NewGuid());

        Assert.Throws<BankAccountAuthorizationException>(
            () => account.TransferOut(Booking, otherCharacter, isAuthorized: false, CreatePersonalAccount(), 10m));
    }

    [Fact]
    public void TransferOut_InsufficientBalance_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 10m);

        Assert.Throws<InsufficientBalanceException>(
            () => account.TransferOut(Booking, Owner, isAuthorized: true, CreatePersonalAccount(), 1000m));
    }

    [Fact]
    public void TransferOut_ToSameAccount_Throws()
    {
        var account = CreatePersonalAccount();
        account.Deposit(Booking, Owner, 100m);

        Assert.Throws<InvalidOperationException>(() => account.TransferOut(Booking, Owner, isAuthorized: true, account, 10m));
    }

    [Fact]
    public void Deposit_RecordsTheDepositorAndBooking()
    {
        var account = CreatePersonalAccount();
        var depositor = new CharacterId(Guid.NewGuid());

        var domainEvent = account.Deposit(Booking, depositor, 10m);

        Assert.Equal(depositor, domainEvent.CharacterId);
        Assert.Same(Booking, domainEvent.Booking);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("atm-1", true)]
    [InlineData("", false)]
    public void IsValidSource_AllowsNullOrOneTo128Characters(string? source, bool expected)
    {
        Assert.Equal(expected, ELifeRPG.Banking.Domain.Booking.IsValidSource(source));
        Assert.True(ELifeRPG.Banking.Domain.Booking.IsValidSource(new string('x', 128)));
        Assert.False(ELifeRPG.Banking.Domain.Booking.IsValidSource(new string('x', 129)));
    }

    [Fact]
    public void ReceiveTransfer_AddsAmount_NoFee()
    {
        var account = CreatePersonalAccount();
        var sourceId = new BankAccountId(Guid.NewGuid());

        var domainEvent = account.ReceiveTransfer(Booking, sourceId, 25m);

        Assert.Equal(sourceId, domainEvent.SourceBankAccountId);
        Assert.Equal(25m, account.Balance);
    }
}
