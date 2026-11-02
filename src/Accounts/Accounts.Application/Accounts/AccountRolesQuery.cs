using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union AccountRolesResult(AccountRolesResult.Found, AccountRolesResult.AccountNotFound, AccountRolesResult.NoWebIdentity)
{
    public record Found(IReadOnlyList<string> AssignedRoles, IReadOnlyList<KeycloakRealmRole> AvailableRoles);

    public record AccountNotFound;

    /// <summary>
    /// The account has no Keycloak user, so there is nothing to manage roles on. A join-first
    /// player who has never linked is in exactly this state. Mapped to 409 rather than 404, which
    /// would lie to staff: the account does exist.
    /// </summary>
    public record NoWebIdentity;
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

        if (account.KeycloakUserId is not { } keycloakUserId)
        {
            return new AccountRolesResult.NoWebIdentity();
        }

        var assignedRoles = await keycloakUserProvisioner.ListUserRealmRolesAsync(keycloakUserId, cancellationToken);
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
