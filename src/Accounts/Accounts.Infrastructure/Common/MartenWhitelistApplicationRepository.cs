using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenWhitelistApplicationRepository(IDocumentSession session) : IWhitelistApplicationRepository
{
    public async ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken)
        => await session.LoadAsync<WhitelistApplication>(id, cancellationToken);

    public async ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>()
            .Where(x => x.AccountId == accountId
                && (x.Status == WhitelistApplicationStatus.Open || x.Status == WhitelistApplicationStatus.InReview))
            .FirstOrDefaultAsync(cancellationToken);

    public async ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>()
            .Where(x => x.AccountId == accountId && x.Status == WhitelistApplicationStatus.Approved)
            .FirstOrDefaultAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken)
        => await session.Query<WhitelistApplication>().Where(x => x.Status == status).ToListAsync(cancellationToken);

    public void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted domainEvent)
        => session.Events.StartStream<WhitelistApplication>(application.Id.Value, domainEvent);

    public void Append<TEvent>(WhitelistApplicationId id, TEvent domainEvent) where TEvent : notnull
        => session.Events.Append(id.Value, domainEvent);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => new(session.SaveChangesAsync(cancellationToken));
}
