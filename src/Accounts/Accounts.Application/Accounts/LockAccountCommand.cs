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
        await keycloakUserProvisioner.DisableUserAsync(account.KeycloakUserId, cancellationToken);

        return new LockAccountResult.Locked();
    }
}
