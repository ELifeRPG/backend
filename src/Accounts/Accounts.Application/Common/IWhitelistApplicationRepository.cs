using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IWhitelistApplicationRepository
{
    ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, string serverClientId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken);

    void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted domainEvent);

    void Append<TEvent>(WhitelistApplicationId id, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
