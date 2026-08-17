using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Whitelist;

public union WhitelistApplicationsResult(WhitelistApplicationsResult.Found)
{
    public record Found(IReadOnlyList<WhitelistApplication> Applications);
}

public sealed record WhitelistApplicationsQuery(WhitelistApplicationStatus Status) : IRequest<WhitelistApplicationsResult>;

public sealed class WhitelistApplicationsHandler(IWhitelistApplicationRepository repository)
    : IRequestHandler<WhitelistApplicationsQuery, WhitelistApplicationsResult>
{
    public async ValueTask<WhitelistApplicationsResult> Handle(WhitelistApplicationsQuery request, CancellationToken cancellationToken)
        => new WhitelistApplicationsResult.Found(await repository.ListByStatusAsync(request.Status, cancellationToken));
}
