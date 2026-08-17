using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union DepositResult(DepositResult.Deposited, DepositResult.BankAccountNotFound)
{
    public record Deposited(decimal Amount, decimal Fee, decimal NewBalance);

    public record BankAccountNotFound;
}

public sealed record DepositCommand(BankAccountId BankAccountId, decimal Amount) : IRequest<DepositResult>;

public sealed class DepositHandler(IBankAccountRepository bankAccountRepository) : IRequestHandler<DepositCommand, DepositResult>
{
    public async ValueTask<DepositResult> Handle(DepositCommand request, CancellationToken cancellationToken)
    {
        var bankAccount = await bankAccountRepository.FindByIdAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new DepositResult.BankAccountNotFound();
        }

        var domainEvent = bankAccount.Deposit(request.Amount);
        bankAccountRepository.Append(request.BankAccountId, domainEvent);
        await bankAccountRepository.SaveChangesAsync(cancellationToken);

        return new DepositResult.Deposited(domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }
}
