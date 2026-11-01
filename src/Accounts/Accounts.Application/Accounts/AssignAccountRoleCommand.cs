using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union AssignAccountRoleResult(
    AssignAccountRoleResult.Assigned,
    AssignAccountRoleResult.AccountNotFound,
    AssignAccountRoleResult.RoleNotFound,
    AssignAccountRoleResult.NoWebIdentity)
{
    public record Assigned;

    public record AccountNotFound;

    public record RoleNotFound;

    /// <summary>
    /// The account has no Keycloak user, so there is nothing to manage roles on. A join-first
    /// player who has never linked is in exactly this state. Mapped to 409 rather than 404, which
    /// would lie to staff: the account does exist.
    /// </summary>
    public record NoWebIdentity;
}

public sealed record AssignAccountRoleCommand(AccountId AccountId, string RoleName) : IRequest<AssignAccountRoleResult>;

public sealed class AssignAccountRoleHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<AssignAccountRoleCommand, AssignAccountRoleResult>
{
    public async ValueTask<AssignAccountRoleResult> Handle(AssignAccountRoleCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new AssignAccountRoleResult.AccountNotFound();
        }

        if (account.KeycloakUserId is not { } keycloakUserId)
        {
            return new AssignAccountRoleResult.NoWebIdentity();
        }

        var assigned = await keycloakUserProvisioner.AssignRealmRoleAsync(keycloakUserId, request.RoleName, cancellationToken);
        return assigned ? new AssignAccountRoleResult.Assigned() : new AssignAccountRoleResult.RoleNotFound();
    }
}
