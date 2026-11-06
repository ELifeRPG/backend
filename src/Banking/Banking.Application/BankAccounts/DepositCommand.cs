using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union DepositResult(DepositResult.Deposited, DepositResult.InvalidAmount, DepositResult.BankAccountNotFound, DepositResult.ConcurrentModification)
{
    public record Deposited(decimal Amount, decimal Fee, decimal NewBalance);

    /// <summary>Amount was not strictly positive or not in whole cents.</summary>
    public record InvalidAmount;

    public record BankAccountNotFound;

    public record ConcurrentModification;
}

public sealed record DepositCommand(BankAccountId BankAccountId, decimal Amount) : IRequest<DepositResult>;

public sealed class DepositHandler(IBankAccountRepository bankAccountRepository) : IRequestHandler<DepositCommand, DepositResult>
{
    public async ValueTask<DepositResult> Handle(DepositCommand request, CancellationToken cancellationToken)
    {
        if (!Money.IsValidAmount(request.Amount))
        {
            return new DepositResult.InvalidAmount();
        }

        var bankAccount = await bankAccountRepository.FetchForUpdateAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new DepositResult.BankAccountNotFound();
        }

        var domainEvent = bankAccount.Deposit(request.Amount);
        bankAccountRepository.Append(request.BankAccountId, domainEvent);

        try
        {
            await bankAccountRepository.SaveChangesAsync(cancellationToken);
        }
        catch (BankAccountConcurrencyException)
        {
            return new DepositResult.ConcurrentModification();
        }

        return new DepositResult.Deposited(domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }
}
