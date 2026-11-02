using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenGameAccountLinkRepository(IDocumentSession session) : IGameAccountLinkRepository
{
    public async ValueTask<GameAccountLink?> FindByGameIdentityIdAsync(GameIdentityId gameIdentityId, CancellationToken cancellationToken)
        => await session.Query<GameAccountLink>()
            .SingleOrDefaultAsync(x => x.GameIdentityId.Value == gameIdentityId.Value, cancellationToken);

    public async ValueTask<IReadOnlyList<GameAccountLink>> ListByAccountAsync(AccountId accountId, CancellationToken cancellationToken)
        => await session.Query<GameAccountLink>()
            .Where(x => x.AccountId.Value == accountId.Value)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<GameAccountLink>> ListByAccountsAsync(
        IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        // In-memory filter rather than a translated Contains over strongly-typed ids: the staff list
        // is already bounded by the account page it was built from. Same volume assumption as
        // SearchAsync below.
        var wanted = accountIds.Select(x => x.Value).ToHashSet();
        var links = await session.Query<GameAccountLink>().ToListAsync(cancellationToken);
        return links.Where(x => wanted.Contains(x.AccountId.Value)).ToList();
    }

    // In-memory filter, not a Marten LINQ-translated Where: GameIdentityId is a Guid, and
    // .ToString().Contains() over the JSONB-stored document isn't a query Marten's LINQ provider
    // translates reliably. Fine at this project's volume; revisit if this ever needs to scale past
    // loading the full table per search. Mirrors what MartenAccountRepository.SearchAsync did before
    // identities moved off the aggregate.
    public async ValueTask<IReadOnlyList<GameAccountLink>> SearchAsync(string search, CancellationToken cancellationToken)
    {
        var links = await session.Query<GameAccountLink>().ToListAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(search))
        {
            return links;
        }

        return links
            .Where(x => x.GameIdentityId.Value.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void Insert(GameAccountLink link) => session.Insert(link);

    public void Update(GameAccountLink link) => session.Store(link);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => new(session.SaveChangesAsync(cancellationToken));
}
