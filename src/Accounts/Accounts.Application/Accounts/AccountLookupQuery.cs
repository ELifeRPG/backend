using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

/// <summary>
/// The only surface other modules should use to reference an Account — see ARCHITECTURE.md §9e.
/// Other modules should not reference Accounts.Domain or Accounts.Infrastructure directly; dispatch
/// this request/response pair via IMediator instead. (Handler classes are public, not internal, purely
/// because the host owns the single generated Mediator dispatcher and needs cross-assembly access to
/// construct them — the boundary is enforced by convention/review here, not by the compiler.)
/// </summary>
public union AccountLookupResult(AccountLookupResult.Found, AccountLookupResult.NotFound)
{
    public record Found(AccountId AccountId, AccountStatus Status);

    public record NotFound;
}

public sealed record AccountLookupQuery(AccountId AccountId) : IRequest<AccountLookupResult>;

public sealed class AccountLookupHandler(IAccountRepository accountRepository) : IRequestHandler<AccountLookupQuery, AccountLookupResult>
{
    public async ValueTask<AccountLookupResult> Handle(AccountLookupQuery request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);

        return account is null
            ? new AccountLookupResult.NotFound()
            : new AccountLookupResult.Found(account.Id, account.Status);
    }
}
