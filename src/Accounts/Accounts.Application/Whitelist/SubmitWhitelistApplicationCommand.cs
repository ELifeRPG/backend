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

public sealed record SubmitWhitelistApplicationCommand(AccountId AccountId, string ServerClientId, string ApplicationText)
    : IRequest<SubmitWhitelistApplicationResult>;

public sealed class SubmitWhitelistApplicationHandler(IAccountRepository accountRepository, IWhitelistApplicationRepository whitelistRepository)
    : IRequestHandler<SubmitWhitelistApplicationCommand, SubmitWhitelistApplicationResult>
{
    public async ValueTask<SubmitWhitelistApplicationResult> Handle(SubmitWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return new SubmitWhitelistApplicationResult.AccountNotFound();
        }

        var pending = await whitelistRepository.FindPendingAsync(request.AccountId, request.ServerClientId, cancellationToken);
        if (pending is not null)
        {
            return new SubmitWhitelistApplicationResult.AlreadyPending();
        }

        var id = new WhitelistApplicationId(Guid.NewGuid());
        var domainEvent = new WhitelistApplicationSubmitted(id, request.AccountId, request.ServerClientId, request.ApplicationText);
        var application = WhitelistApplication.Create(domainEvent);

        whitelistRepository.StartStream(application, domainEvent);
        await whitelistRepository.SaveChangesAsync(cancellationToken);

        return new SubmitWhitelistApplicationResult.Submitted(id);
    }
}
