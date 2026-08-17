using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public sealed record BankAccountsByCompanyQuery(CompanyId CompanyId) : IRequest<IReadOnlyList<BankAccount>>;

public sealed class BankAccountsByCompanyHandler(IBankAccountRepository bankAccountRepository)
    : IRequestHandler<BankAccountsByCompanyQuery, IReadOnlyList<BankAccount>>
{
    public async ValueTask<IReadOnlyList<BankAccount>> Handle(BankAccountsByCompanyQuery request, CancellationToken cancellationToken)
        => await bankAccountRepository.FindByCompanyIdAsync(request.CompanyId, cancellationToken);
}
