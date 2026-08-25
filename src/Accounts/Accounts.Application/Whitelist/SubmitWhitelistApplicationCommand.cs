using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union SubmitWhitelistApplicationResult(
    SubmitWhitelistApplicationResult.Submitted,
    SubmitWhitelistApplicationResult.AccountNotFound,
    SubmitWhitelistApplicationResult.AlreadyPending)
{
    public record Submitted(WhitelistApplicationId WhitelistApplicationId);

    public record AccountNotFound;

    public record AlreadyPending;
}

/// <summary>
/// Submitted by the player for themselves. The account is derived from the caller's Keycloak
/// subject rather than taken from the request: the previous shape accepted an arbitrary AccountId,
/// which let any holder of the gameserver whitelist scope submit on behalf of any account.
///
/// A player can apply before ever joining the gameserver — that is the whole point of the
/// portal-first flow — so this deliberately does not require a linked Bohemia ID.
/// </summary>
public sealed record SubmitWhitelistApplicationCommand(string ApplicationText)
    : IRequest<SubmitWhitelistApplicationResult>;

public sealed class SubmitWhitelistApplicationHandler(
    IAccountRepository accountRepository,
    IWhitelistApplicationRepository whitelistRepository,
    ICurrentKeycloakUser currentUser)
    : IRequestHandler<SubmitWhitelistApplicationCommand, SubmitWhitelistApplicationResult>
{
    public async ValueTask<SubmitWhitelistApplicationResult> Handle(SubmitWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var keycloakUserId = await currentUser.GetIdAsync(cancellationToken);
        if (keycloakUserId is not { } subject)
        {
            return new SubmitWhitelistApplicationResult.AccountNotFound();
        }

        var account = await accountRepository.FindByKeycloakUserIdAsync(subject, cancellationToken);
        if (account is null)
        {
            return new SubmitWhitelistApplicationResult.AccountNotFound();
        }

        var pending = await whitelistRepository.FindPendingAsync(account.Id, cancellationToken);
        if (pending is not null)
        {
            return new SubmitWhitelistApplicationResult.AlreadyPending();
        }

        var id = new WhitelistApplicationId(Guid.NewGuid());
        var domainEvent = new WhitelistApplicationSubmitted(id, account.Id, request.ApplicationText);
        var application = WhitelistApplication.Create(domainEvent);

        whitelistRepository.StartStream(application, domainEvent);
        await whitelistRepository.SaveChangesAsync(cancellationToken);

        return new SubmitWhitelistApplicationResult.Submitted(id);
    }
}
