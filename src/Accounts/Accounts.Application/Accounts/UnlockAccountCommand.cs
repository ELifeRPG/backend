using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union UnlockAccountResult(UnlockAccountResult.Unlocked, UnlockAccountResult.AccountNotFound)
{
    public record Unlocked;

    public record AccountNotFound;
}

public sealed record UnlockAccountCommand(AccountId AccountId) : IRequest<UnlockAccountResult>;

public sealed class UnlockAccountHandler(IAccountRepository accountRepository, IKeycloakUserProvisioner keycloakUserProvisioner)
    : IRequestHandler<UnlockAccountCommand, UnlockAccountResult>
{
    public async ValueTask<UnlockAccountResult> Handle(UnlockAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new UnlockAccountResult.AccountNotFound();
        }

        if (account.Status == AccountStatus.Locked)
        {
            var domainEvent = account.Unlock();
            accountRepository.Append(request.AccountId, domainEvent);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        await keycloakUserProvisioner.EnableUserAsync(account.KeycloakUserId, cancellationToken);

        return new UnlockAccountResult.Unlocked();
    }
}
