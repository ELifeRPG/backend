using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union StartWhitelistApplicationReviewResult(StartWhitelistApplicationReviewResult.Started, StartWhitelistApplicationReviewResult.NotFound)
{
    public record Started;

    public record NotFound;
}

public sealed record StartWhitelistApplicationReviewCommand(WhitelistApplicationId Id) : IRequest<StartWhitelistApplicationReviewResult>;

public sealed class StartWhitelistApplicationReviewHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<StartWhitelistApplicationReviewCommand, StartWhitelistApplicationReviewResult>
{
    public async ValueTask<StartWhitelistApplicationReviewResult> Handle(StartWhitelistApplicationReviewCommand request, CancellationToken cancellationToken)
    {
        var application = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (application is null)
        {
            return new StartWhitelistApplicationReviewResult.NotFound();
        }

        var domainEvent = application.StartReview();
        if (domainEvent is not null)
        {
            repository.Append(request.Id, domainEvent);
            await repository.SaveChangesAsync(cancellationToken);
        }

        return new StartWhitelistApplicationReviewResult.Started();
    }
}
