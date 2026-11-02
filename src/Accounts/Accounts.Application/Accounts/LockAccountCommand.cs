using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union LockAccountResult(LockAccountResult.Locked, LockAccountResult.AccountNotFound)
{
    public record Locked;

    public record AccountNotFound;
}

public sealed record LockAccountCommand(AccountId AccountId) : IRequest<LockAccountResult>;

public sealed class LockAccountHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<LockAccountCommand, LockAccountResult>
{
    public async ValueTask<LockAccountResult> Handle(LockAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new LockAccountResult.AccountNotFound();
        }

        if (account.Status == AccountStatus.Active)
        {
            var domainEvent = account.Lock();
            accountRepository.Append(request.AccountId, domainEvent);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        // Disabling on every call (not just on the first lock) makes this safe to retry if a
        // previous lock's Keycloak call failed after the domain state already committed.
        //
        // A join-first account has no Keycloak user to disable, and locking it must still work —
        // this is the ban button, and the gameserver gate reads Status, not Keycloak's enabled flag.
        if (account.KeycloakUserId is { } keycloakUserId)
        {
            await keycloakUserProvisioner.DisableUserAsync(keycloakUserId, cancellationToken);
        }

        return new LockAccountResult.Locked();
    }
}
