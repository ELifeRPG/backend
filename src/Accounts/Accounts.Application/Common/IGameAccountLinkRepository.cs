namespace ELifeRPG.Accounts.Application.Common;

/// <summary>
/// Game-account links: the many side of one account to many in-game identities.
///
/// Shares the module's scoped <c>IDocumentSession</c> with <see cref="IAccountRepository"/>, so a
/// <c>SaveChangesAsync</c> on either commits both. That is load-bearing and not obvious: join-first
/// creation writes an <c>Account</c> stream and a <see cref="GameAccountLink"/> document in one
/// transaction, so a duplicate link rolls the new account back with it.
/// </summary>
public interface IGameAccountLinkRepository
{
    /// <summary>
    /// Safe as a single-result query because <c>GameIdentityId</c> carries a unique index — the same
    /// reason <c>IGameServerRepository.FindByClientIdAsync</c> is safe.
    /// </summary>
    ValueTask<GameAccountLink?> FindByGameIdentityIdAsync(GameIdentityId gameIdentityId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameAccountLink>> ListByAccountAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameAccountLink>> ListByAccountsAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken);

    /// <summary>Links whose game identity contains <paramref name="search"/>, for the staff account list.</summary>
    ValueTask<IReadOnlyList<GameAccountLink>> SearchAsync(string search, CancellationToken cancellationToken);

    /// <summary>
    /// A true insert, not an upsert: a second link for the same game identity must fail on the
    /// unique index rather than silently overwrite the first. This is what makes two concurrent
    /// joins for one player resolve to one account.
    /// </summary>
    void Insert(GameAccountLink link);

    void Update(GameAccountLink link);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
