using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Accounts;

public sealed record EnsureAccountForKeycloakUserResult(AccountId AccountId, bool Created);

/// <summary>
/// Creates the account behind a Keycloak user, if it does not exist yet. This is the portal-first
/// entry point: the Keycloak user is created by ordinary web signup (Discord broker or local
/// registration) and the account is created the first time that user reaches the backend — well
/// before they ever join the gameserver, and therefore with no Bohemia ID.
///
/// Idempotent: joining the portal twice, or a page that calls this on every load, returns the same
/// account rather than creating a second one.
/// </summary>
public sealed record EnsureAccountForKeycloakUserCommand(KeycloakUserId KeycloakUserId)
    : IRequest<EnsureAccountForKeycloakUserResult>;

public sealed class EnsureAccountForKeycloakUserHandler(IAccountRepository accountRepository)
    : IRequestHandler<EnsureAccountForKeycloakUserCommand, EnsureAccountForKeycloakUserResult>
{
    public async ValueTask<EnsureAccountForKeycloakUserResult> Handle(
        EnsureAccountForKeycloakUserCommand request, CancellationToken cancellationToken)
    {
        var existing = await accountRepository.FindByKeycloakUserIdAsync(request.KeycloakUserId, cancellationToken);
        if (existing is not null)
        {
            return new EnsureAccountForKeycloakUserResult(existing.Id, Created: false);
        }

        var accountId = new AccountId(Guid.NewGuid());
        var domainEvent = new AccountCreated(accountId, BohemiaId: null, request.KeycloakUserId);

        var account = Account.Create(domainEvent);
        accountRepository.StartStream(account, domainEvent);
        await accountRepository.SaveChangesAsync(cancellationToken);

        return new EnsureAccountForKeycloakUserResult(accountId, Created: true);
    }
}
