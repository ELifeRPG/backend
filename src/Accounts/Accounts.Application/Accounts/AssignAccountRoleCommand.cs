using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union AssignAccountRoleResult(AssignAccountRoleResult.Assigned, AssignAccountRoleResult.AccountNotFound, AssignAccountRoleResult.RoleNotFound)
{
    public record Assigned;

    public record AccountNotFound;

    public record RoleNotFound;
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

        var assigned = await keycloakUserProvisioner.AssignRealmRoleAsync(account.KeycloakUserId, request.RoleName, cancellationToken);
        return assigned ? new AssignAccountRoleResult.Assigned() : new AssignAccountRoleResult.RoleNotFound();
    }
}
