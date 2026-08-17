using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union AccountRolesResult(AccountRolesResult.Found, AccountRolesResult.AccountNotFound)
{
    public record Found(IReadOnlyList<string> AssignedRoles, IReadOnlyList<KeycloakRealmRole> AvailableRoles);

    public record AccountNotFound;
}

public sealed record AccountRolesQuery(AccountId AccountId) : IRequest<AccountRolesResult>;

public sealed class AccountRolesHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<AccountRolesQuery, AccountRolesResult>
{
    public async ValueTask<AccountRolesResult> Handle(AccountRolesQuery request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new AccountRolesResult.AccountNotFound();
        }

        var assignedRoles = await keycloakUserProvisioner.ListUserRealmRolesAsync(account.KeycloakUserId, cancellationToken);
        if (assignedRoles is null)
        {
            // The account exists in Postgres, but its Keycloak user doesn't (e.g. deleted
            // out-of-band). From the API consumer's perspective this is the same symptom as an
            // unknown account — roles can't be managed for it either way.
            return new AccountRolesResult.AccountNotFound();
        }

        var availableRoles = await keycloakUserProvisioner.ListRealmRolesAsync(cancellationToken);
        return new AccountRolesResult.Found(assignedRoles, availableRoles);
    }
}
