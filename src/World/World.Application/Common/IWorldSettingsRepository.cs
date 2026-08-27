namespace ELifeRPG.World.Application.Common;

public interface IWorldSettingsRepository
{
    ValueTask<WorldSettings> GetAsync(CancellationToken cancellationToken);
}
