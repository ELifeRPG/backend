using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IWhitelistApplicationRepository
{
    ValueTask<WhitelistApplication?> FindByIdAsync(WhitelistApplicationId id, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindPendingAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<WhitelistApplication?> FindApprovedAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<WhitelistApplication>> ListByStatusAsync(WhitelistApplicationStatus status, CancellationToken cancellationToken);

    /// <summary>
    /// Every application for an account, of every status. The absorb re-points all of them, and
    /// Approved is the one that matters most: left behind, an already-whitelisted player reads as
    /// not_whitelisted on their next join.
    /// </summary>
    ValueTask<IReadOnlyList<WhitelistApplication>> ListByAccountAsync(AccountId accountId, CancellationToken cancellationToken);

    void StartStream(WhitelistApplication application, WhitelistApplicationSubmitted domainEvent);

    void Append<TEvent>(WhitelistApplicationId id, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
