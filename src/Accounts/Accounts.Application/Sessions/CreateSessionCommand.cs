using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Sessions;

public enum SessionStatus
{
    Active,
    Blocked,
    NotWhitelisted,
}

public sealed record CreateSessionResponse(AccountId AccountId, string KeycloakUsername, SessionStatus Status);

public sealed record CreateSessionCommand(GameId BohemiaId, string ServerClientId) : IRequest<CreateSessionResponse>;

public sealed class CreateSessionHandler(
    IAccountRepository accountRepository,
    IKeycloakUserProvisioner keycloakUserProvisioner,
    IGameServerRepository gameServerRepository,
    IWhitelistApplicationRepository whitelistRepository)
    : IRequestHandler<CreateSessionCommand, CreateSessionResponse>
{
    public async ValueTask<CreateSessionResponse> Handle(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByBohemiaIdAsync(request.BohemiaId, cancellationToken);

        if (account is null)
        {
            var keycloakUserId = await keycloakUserProvisioner.EnsureUserAsync(request.BohemiaId, cancellationToken);
            var accountId = new AccountId(Guid.NewGuid());
            var domainEvent = new AccountCreated(accountId, request.BohemiaId, keycloakUserId);

            account = Account.Create(domainEvent);
            accountRepository.StartStream(account, domainEvent);
            await accountRepository.SaveChangesAsync(cancellationToken);
        }

        var status = await ResolveStatusAsync(account, request.ServerClientId, cancellationToken);

        return new CreateSessionResponse(account.Id, KeycloakUsername.For(account.BohemiaId), status);
    }

    private async ValueTask<SessionStatus> ResolveStatusAsync(Account account, string serverClientId, CancellationToken cancellationToken)
    {
        if (account.Status == AccountStatus.Locked)
        {
            return SessionStatus.Blocked;
        }

        var server = await gameServerRepository.GetOrDefaultAsync(serverClientId, cancellationToken);
        if (!server.WhitelistEnabled)
        {
            return SessionStatus.Active;
        }

        var approved = await whitelistRepository.FindApprovedAsync(account.Id, serverClientId, cancellationToken);
        return approved is null ? SessionStatus.NotWhitelisted : SessionStatus.Active;
    }
}
