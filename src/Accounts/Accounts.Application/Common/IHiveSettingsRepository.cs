namespace ELifeRPG.Accounts.Application.Common;

public interface IHiveSettingsRepository
{
    ValueTask<HiveSettings> GetAsync(CancellationToken cancellationToken);

    ValueTask UpsertAsync(HiveSettings settings, CancellationToken cancellationToken);
}
