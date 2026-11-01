using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union RevokeAccountRoleResult(
    RevokeAccountRoleResult.Revoked,
    RevokeAccountRoleResult.AccountNotFound,
    RevokeAccountRoleResult.RoleNotFound,
    RevokeAccountRoleResult.NoWebIdentity)
{
    public record Revoked;

    public record AccountNotFound;

    public record RoleNotFound;

    /// <summary>
    /// The account has no Keycloak user, so there is nothing to manage roles on. A join-first
    /// player who has never linked is in exactly this state. Mapped to 409 rather than 404, which
    /// would lie to staff: the account does exist.
    /// </summary>
    public record NoWebIdentity;
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

        if (account.KeycloakUserId is not { } keycloakUserId)
        {
            return new RevokeAccountRoleResult.NoWebIdentity();
        }

        var revoked = await keycloakUserProvisioner.RemoveRealmRoleAsync(keycloakUserId, request.RoleName, cancellationToken);
        return revoked ? new RevokeAccountRoleResult.Revoked() : new RevokeAccountRoleResult.RoleNotFound();
    }
}
