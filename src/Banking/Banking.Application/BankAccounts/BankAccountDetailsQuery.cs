using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union BankAccountDetailsResult(BankAccountDetailsResult.Found, BankAccountDetailsResult.NotFound)
{
    public record Found(BankAccount BankAccount);

    public record NotFound;
}

public sealed record BankAccountDetailsQuery(BankAccountId BankAccountId) : IRequest<BankAccountDetailsResult>;

public sealed class BankAccountDetailsHandler(IBankAccountRepository bankAccountRepository)
    : IRequestHandler<BankAccountDetailsQuery, BankAccountDetailsResult>
{
    public async ValueTask<BankAccountDetailsResult> Handle(BankAccountDetailsQuery request, CancellationToken cancellationToken)
    {
        var bankAccount = await bankAccountRepository.FindByIdAsync(request.BankAccountId, cancellationToken);
        return bankAccount is null
            ? new BankAccountDetailsResult.NotFound()
            : new BankAccountDetailsResult.Found(bankAccount);
    }
}
