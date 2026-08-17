using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union AccountsResult(AccountsResult.Found)
{
    public record Found(IReadOnlyList<Account> Accounts);
}

public sealed record AccountsQuery(string Search) : IRequest<AccountsResult>;

public sealed class AccountsHandler(IAccountRepository accountRepository) : IRequestHandler<AccountsQuery, AccountsResult>
{
    public async ValueTask<AccountsResult> Handle(AccountsQuery request, CancellationToken cancellationToken)
        => new AccountsResult.Found(await accountRepository.SearchAsync(request.Search, cancellationToken));
}
