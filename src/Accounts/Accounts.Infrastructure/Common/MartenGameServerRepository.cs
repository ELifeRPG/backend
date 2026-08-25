using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenGameServerRepository(IDocumentSession session) : IGameServerRepository
{
    // No implicit defaulting: an unregistered client_id is now an error, not a silently-defaulted
    // server. See docs/superpowers/specs/2026-08-22-hive-tenancy-design.md, Part 2.
    public async ValueTask<GameServer?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken)
        => await session.Query<GameServer>().SingleOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);

    public async ValueTask<IReadOnlyList<GameServer>> ListAsync(CancellationToken cancellationToken)
        => await session.Query<GameServer>().ToListAsync(cancellationToken);

    public async ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken)
    {
        session.Store(server);
        await session.SaveChangesAsync(cancellationToken);
    }
}
