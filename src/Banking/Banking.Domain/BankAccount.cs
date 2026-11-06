using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Domain;

public class BankAccount
{
    public BankAccountId Id { get; private set; }

    public BankId BankId { get; private set; }

    public BankAccountType Type { get; private set; }

    public CharacterId? OwnerCharacterId { get; private set; }

    public CompanyId? OwnerCompanyId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public decimal Balance { get; private set; }

    /// <summary>
    /// Snapshotted from the Bank's condition at the moment the account was opened — matches the
    /// legacy app's BankAccount.BankCondition, which is fixed to the bank's first condition at
    /// open time and never re-read from the bank afterward.
    /// </summary>
    public decimal TransactionFeeBase { get; private set; }

    public decimal TransactionFeeMultiplier { get; private set; }

    public static BankAccount Create(BankAccountOpened domainEvent)
    {
        var account = new BankAccount();
        account.Apply(domainEvent);
        return account;
    }

    /// <summary>Anyone may deposit cash into any account; actingCharacterId is recorded for the ledger, not checked.</summary>
    public BankAccountDeposited Deposit(Booking booking, CharacterId actingCharacterId, decimal amount)
    {
        EnsureValidAmount(amount);

        var domainEvent = new BankAccountDeposited(Id, amount, CalculateFee(amount), actingCharacterId, booking);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// isAuthorized is resolved by the caller (Banking.Application), not this aggregate: for a
    /// Personal account it's "does actingCharacterId own this account"; for a Corporate account
    /// it's "is actingCharacterId a company member with ManageFinances permission" — the latter
    /// requires a cross-module query into Companies, which Domain may never do. actingCharacterId
    /// itself is always recorded on the resulting event for audit purposes, regardless of ownership
    /// type. See ARCHITECTURE.md §9e.
    /// </summary>
    public BankAccountWithdrawn Withdraw(Booking booking, CharacterId actingCharacterId, bool isAuthorized, decimal amount)
    {
        EnsureAuthorized(isAuthorized);
        EnsureValidAmount(amount);

        var fee = CalculateFee(amount);
        EnsureSufficientBalance(amount, fee);

        var domainEvent = new BankAccountWithdrawn(Id, amount, fee, actingCharacterId, booking);
        Apply(domainEvent);
        return domainEvent;
    }

    public BankAccountTransferredOut TransferOut(Booking booking, CharacterId actingCharacterId, bool isAuthorized, BankAccountId targetBankAccountId, decimal amount)
    {
        EnsureAuthorized(isAuthorized);
        EnsureValidAmount(amount);

        if (targetBankAccountId == Id)
        {
            throw new InvalidOperationException("Can not transfer to the same bank account.");
        }

        var fee = CalculateFee(amount);
        EnsureSufficientBalance(amount, fee);

        var domainEvent = new BankAccountTransferredOut(Id, targetBankAccountId, amount, fee, actingCharacterId, booking);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Applied to the *target* account of a transfer — no fee is charged to the receiving side,
    /// matching the legacy app's booking split (fees only ever land on the initiating account).
    /// Takes the same booking as the source's TransferOut, so both legs share one transaction id.
    /// </summary>
    public BankAccountTransferredIn ReceiveTransfer(Booking booking, BankAccountId sourceBankAccountId, decimal amount)
    {
        var domainEvent = new BankAccountTransferredIn(Id, sourceBankAccountId, amount, booking);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(BankAccountOpened domainEvent)
    {
        Id = domainEvent.Id;
        BankId = domainEvent.BankId;
        Type = domainEvent.Type;
        OwnerCharacterId = domainEvent.OwnerCharacterId;
        OwnerCompanyId = domainEvent.OwnerCompanyId;
        Number = domainEvent.Number;
        TransactionFeeBase = domainEvent.TransactionFeeBase;
        TransactionFeeMultiplier = domainEvent.TransactionFeeMultiplier;
    }

    public void Apply(BankAccountDeposited domainEvent) => Balance += domainEvent.Amount - domainEvent.Fee;

    public void Apply(BankAccountWithdrawn domainEvent) => Balance -= domainEvent.Amount + domainEvent.Fee;

    public void Apply(BankAccountTransferredOut domainEvent) => Balance -= domainEvent.Amount + domainEvent.Fee;

    public void Apply(BankAccountTransferredIn domainEvent) => Balance += domainEvent.Amount;

    private static void EnsureAuthorized(bool isAuthorized)
    {
        if (!isAuthorized)
        {
            throw new BankAccountAuthorizationException("Character is not authorized to transact on this bank account.");
        }
    }

    private void EnsureSufficientBalance(decimal amount, decimal fee)
    {
        if (Balance < amount + fee)
        {
            throw new InsufficientBalanceException("Can not withdraw money due to low account balance.");
        }
    }

    /// <summary>
    /// Backstop only: the Application handlers reject an invalid amount as a 400 before it gets here.
    /// </summary>
    private static void EnsureValidAmount(decimal amount)
    {
        if (!Money.IsValidAmount(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Amount must be positive and in whole cents.");
        }
    }

    /// <summary>Rounded to whole cents at booking time, using the rule documented on <see cref="Money"/>.</summary>
    private decimal CalculateFee(decimal amount) => Money.RoundToCents(TransactionFeeBase + (amount * TransactionFeeMultiplier));
}
