using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenHiveSettingsRepository(IDocumentSession session) : IHiveSettingsRepository
{
    public async ValueTask<HiveSettings> GetAsync(CancellationToken cancellationToken)
        => await session.LoadAsync<HiveSettings>(HiveSettings.SingletonId, cancellationToken)
            ?? new HiveSettings();

    public async ValueTask UpsertAsync(HiveSettings settings, CancellationToken cancellationToken)
    {
        session.Store(settings);
        await session.SaveChangesAsync(cancellationToken);
    }
}
