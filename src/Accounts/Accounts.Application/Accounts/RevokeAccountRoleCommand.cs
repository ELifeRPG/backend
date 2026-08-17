using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union RevokeAccountRoleResult(RevokeAccountRoleResult.Revoked, RevokeAccountRoleResult.AccountNotFound, RevokeAccountRoleResult.RoleNotFound)
{
    public record Revoked;

    public record AccountNotFound;

    public record RoleNotFound;
}

public sealed record RevokeAccountRoleCommand(AccountId AccountId, string RoleName) : IRequest<RevokeAccountRoleResult>;

public sealed class RevokeAccountRoleHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<RevokeAccountRoleCommand, RevokeAccountRoleResult>
{
    public async ValueTask<RevokeAccountRoleResult> Handle(RevokeAccountRoleCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new RevokeAccountRoleResult.AccountNotFound();
        }

        var revoked = await keycloakUserProvisioner.RemoveRealmRoleAsync(account.KeycloakUserId, request.RoleName, cancellationToken);
        return revoked ? new RevokeAccountRoleResult.Revoked() : new RevokeAccountRoleResult.RoleNotFound();
    }
}
