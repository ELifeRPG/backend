namespace ELifeRPG.World.Application.Common;

public interface IWorldSettingsRepository
{
    /// <summary>
    /// Reads the singleton, falling back to an unstored <see cref="WorldSettings"/> (every knob at its
    /// property initializer) when nothing has been written yet. Deliberately a single
    /// <c>LoadAsync</c> by primary key and nothing more: this sits on the hot snapshot path — every
    /// <c>POST /api/inventory/snapshots</c> reads it before it touches a row — so it must stay one
    /// point lookup no matter what the write side below grows into.
    /// </summary>
    ValueTask<WorldSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Persists the singleton, mirroring <c>Accounts.Application.Common.IHiveSettingsRepository</c>.
    /// The document is the whole unit — <c>UpdateWorldSettingsHandler</c> is what makes an update
    /// partial, by reading through <see cref="GetAsync"/> first and only overwriting the knobs the
    /// caller named.
    /// </summary>
    ValueTask UpsertAsync(WorldSettings settings, CancellationToken cancellationToken);
}
