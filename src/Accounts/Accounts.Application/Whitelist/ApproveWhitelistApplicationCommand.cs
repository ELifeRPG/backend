using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union ApproveWhitelistApplicationResult(
    ApproveWhitelistApplicationResult.Approved,
    ApproveWhitelistApplicationResult.NotFound,
    ApproveWhitelistApplicationResult.InvalidState)
{
    public record Approved;

    public record NotFound;

    public record InvalidState;
}

public sealed record ApproveWhitelistApplicationCommand(WhitelistApplicationId Id) : IRequest<ApproveWhitelistApplicationResult>;

public sealed class ApproveWhitelistApplicationHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<ApproveWhitelistApplicationCommand, ApproveWhitelistApplicationResult>
{
    public async ValueTask<ApproveWhitelistApplicationResult> Handle(ApproveWhitelistApplicationCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new ApproveWhitelistApplicationResult.NotFound();
        }

        try
        {
            var domainEvent = application.Approve();
            if (domainEvent is not null)
            {
                repository.Append(request.Id, domainEvent);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (WhitelistApplicationStatusException)
        {
            return new ApproveWhitelistApplicationResult.InvalidState();
        }

        return new ApproveWhitelistApplicationResult.Approved();
    }
}
