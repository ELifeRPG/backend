using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union WithdrawResult(WithdrawResult.Withdrawn, WithdrawResult.BankAccountNotFound, WithdrawResult.NotAuthorized, WithdrawResult.InsufficientBalance)
{
    public record Withdrawn(decimal Amount, decimal Fee, decimal NewBalance);

    public record BankAccountNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;
}

public sealed record WithdrawCommand(BankAccountId BankAccountId, CharacterId CharacterId, decimal Amount) : IRequest<WithdrawResult>;

public sealed class WithdrawHandler(IBankAccountRepository bankAccountRepository, IMediator mediator) : IRequestHandler<WithdrawCommand, WithdrawResult>
{
    public async ValueTask<WithdrawResult> Handle(WithdrawCommand request, CancellationToken cancellationToken)
    {
        var bankAccount = await bankAccountRepository.FindByIdAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new WithdrawResult.BankAccountNotFound();
        }

        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(bankAccount, request.CharacterId, mediator, cancellationToken);

        BankAccountWithdrawn domainEvent;
        try
        {
            domainEvent = bankAccount.Withdraw(request.CharacterId, isAuthorized, request.Amount);
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
        await bankAccountRepository.SaveChangesAsync(cancellationToken);

        return new WithdrawResult.Withdrawn(domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }
}
