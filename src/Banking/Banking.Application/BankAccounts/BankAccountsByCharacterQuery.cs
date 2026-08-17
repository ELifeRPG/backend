using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public sealed record BankAccountsByCharacterQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<BankAccount>>;

public sealed class BankAccountsByCharacterHandler(IBankAccountRepository bankAccountRepository)
    : IRequestHandler<BankAccountsByCharacterQuery, IReadOnlyList<BankAccount>>
{
    public async ValueTask<IReadOnlyList<BankAccount>> Handle(BankAccountsByCharacterQuery request, CancellationToken cancellationToken)
        => await bankAccountRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);
}
