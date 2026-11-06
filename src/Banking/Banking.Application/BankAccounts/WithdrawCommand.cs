using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union WithdrawResult(
    WithdrawResult.Withdrawn,
    WithdrawResult.InvalidAmount,
    WithdrawResult.InvalidSource,
    WithdrawResult.BankAccountNotFound,
    WithdrawResult.NotAuthorized,
    WithdrawResult.InsufficientBalance,
    WithdrawResult.ConcurrentModification)
{
    public record Withdrawn(BankTransactionId TransactionId, decimal Amount, decimal Fee, decimal NewBalance);

    /// <summary>Amount was not strictly positive or not in whole cents.</summary>
    public record InvalidAmount;

    /// <summary>Source was empty or longer than <see cref="Booking.MaxSourceLength"/> characters.</summary>
    public record InvalidSource;

    public record BankAccountNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;

    public record ConcurrentModification;
}

public sealed record WithdrawCommand(BankAccountId BankAccountId, CharacterId CharacterId, decimal Amount, string? Source = null) : IRequest<WithdrawResult>;

public sealed class WithdrawHandler(IBankAccountRepository bankAccountRepository, IMediator mediator, ICurrentGameServer currentGameServer) : IRequestHandler<WithdrawCommand, WithdrawResult>
{
    public async ValueTask<WithdrawResult> Handle(WithdrawCommand request, CancellationToken cancellationToken)
    {
        if (!Money.IsValidAmount(request.Amount))
        {
            return new WithdrawResult.InvalidAmount();
        }

        if (!Booking.IsValidSource(request.Source))
        {
            return new WithdrawResult.InvalidSource();
        }

        var bankAccount = await bankAccountRepository.FetchForUpdateAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new WithdrawResult.BankAccountNotFound();
        }

        var booking = Booking.New(await currentGameServer.GetIdAsync(cancellationToken), request.Source);
        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(bankAccount, request.CharacterId, mediator, cancellationToken);

        BankAccountWithdrawn domainEvent;
        try
        {
            domainEvent = bankAccount.Withdraw(booking, request.CharacterId, isAuthorized, request.Amount);
        }
        catch (BankAccountAuthorizationException)
        {
            return new WithdrawResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new WithdrawResult.InsufficientBalance();
        }

        bankAccountRepository.Append(request.BankAccountId, domainEvent);

        try
        {
            await bankAccountRepository.SaveChangesAsync(cancellationToken);
        }
        catch (BankAccountConcurrencyException)
        {
            return new WithdrawResult.ConcurrentModification();
        }

        return new WithdrawResult.Withdrawn(booking.TransactionId, domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }
}
