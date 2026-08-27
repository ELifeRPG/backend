using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain;
using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>Mirrors <c>Accounts.Infrastructure.Common.MartenHiveSettingsRepository</c>, joining the shared <see cref="IWorldSession"/> like every other World repository.</summary>
public sealed class MartenWorldSettingsRepository(IWorldSession worldSession) : IWorldSettingsRepository
{
    private readonly IDocumentSession _session = worldSession.Session;

    public async ValueTask<WorldSettings> GetAsync(CancellationToken cancellationToken)
        => await _session.LoadAsync<WorldSettings>(WorldSettings.SingletonId, cancellationToken)
            ?? new WorldSettings();
}
