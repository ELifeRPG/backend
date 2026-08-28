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

    /// <summary>
    /// A whole-document <c>Store()</c>, which is safe here and nowhere else in this module: global
    /// constraint 4 bans document replacement on <c>ItemInstance</c> because concurrent writers race
    /// per-field, whereas this is a single admin-edited singleton with one writer at a time and no
    /// field any other code path mutates independently. <c>HiveSettings</c> does exactly the same.
    ///
    /// Note that <see cref="IWorldSession"/> is a shared unit of work, so this commits anything else
    /// the scope has pending — the settings command is the only thing in its own request, so that is
    /// never reached in practice, but it is the same caveat every other World repository carries.
    /// </summary>
    public async ValueTask UpsertAsync(WorldSettings settings, CancellationToken cancellationToken)
    {
        _session.Store(settings);
        await _session.SaveChangesAsync(cancellationToken);
    }
}
