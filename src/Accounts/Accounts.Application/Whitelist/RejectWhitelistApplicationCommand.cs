using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union RejectWhitelistApplicationResult(
    RejectWhitelistApplicationResult.Rejected,
    RejectWhitelistApplicationResult.NotFound,
    RejectWhitelistApplicationResult.InvalidState)
{
    public record Rejected;

    public record NotFound;

    public record InvalidState;
}

public sealed record RejectWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<RejectWhitelistApplicationResult>;

public sealed class RejectWhitelistApplicationHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<RejectWhitelistApplicationCommand, RejectWhitelistApplicationResult>
{
    public async ValueTask<RejectWhitelistApplicationResult> Handle(RejectWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new RejectWhitelistApplicationResult.NotFound();
        }

        try
        {
            var domainEvent = application.Reject();
            if (domainEvent is not null)
            {
                repository.Append(request.Id, domainEvent);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (WhitelistApplicationStatusException)
        {
            return new RejectWhitelistApplicationResult.InvalidState();
        }

        return new RejectWhitelistApplicationResult.Rejected();
    }
}
