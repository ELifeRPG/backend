using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenGameServerRepository(IDocumentSession session) : IGameServerRepository
{
    public async ValueTask<GameServer> GetOrDefaultAsync(string clientId, CancellationToken cancellationToken)
        => await session.LoadAsync<GameServer>(clientId, cancellationToken)
            ?? new GameServer { ClientId = clientId, WhitelistEnabled = false };

    public async ValueTask UpsertAsync(GameServer server, CancellationToken cancellationToken)
    {
        session.Store(server);
        await session.SaveChangesAsync(cancellationToken);
    }
}
