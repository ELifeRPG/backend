using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public sealed record AccountWithGameAccounts(Account Account, IReadOnlyList<GameAccountLink> GameAccounts);

public union AccountsResult(AccountsResult.Found)
{
    public record Found(IReadOnlyList<AccountWithGameAccounts> Accounts);
}

public sealed record AccountsQuery(string Search) : IRequest<AccountsResult>;

/// <summary>
/// The staff account list. Search matches on game identity, which now lives on the link documents
/// rather than the account — so a non-empty search resolves links first and then the accounts behind
/// them, and an account with no links can only be reached by listing everything.
/// </summary>
public sealed class AccountsHandler(
    IAccountRepository accountRepository,
    IGameAccountLinkRepository linkRepository)
    : IRequestHandler<AccountsQuery, AccountsResult>
{
    public async ValueTask<AccountsResult> Handle(AccountsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Account> accounts;

        if (string.IsNullOrWhiteSpace(request.Search))
        {
            accounts = await accountRepository.ListAsync(cancellationToken);
        }
        else
        {
            var matches = await linkRepository.SearchAsync(request.Search, cancellationToken);
            var accountIds = matches.Select(x => x.AccountId).DistinctBy(x => x.Value).ToList();
            accounts = await accountRepository.ListByIdsAsync(accountIds, cancellationToken);
        }

        var links = await linkRepository.ListByAccountsAsync(
            accounts.Select(x => x.Id).ToList(), cancellationToken);
        var linksByAccount = links.ToLookup(x => x.AccountId.Value);

        return new AccountsResult.Found(
            accounts
                .Select(account => new AccountWithGameAccounts(account, linksByAccount[account.Id.Value].ToList()))
                .ToList());
    }
}
