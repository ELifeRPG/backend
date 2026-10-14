using ELifeRPG.Phone.Application.Apps.Store;

namespace ELifeRPG.Phone.Api.Apps.Store;

/// <summary>
/// A store entry as a client sees it. No <c>IsSystemApp</c>: the store lists only what it can act
/// on, so the field would be a constant false on every row.
/// </summary>
public sealed record StoreAppDto(string Key, string DisplayName, bool IsInstalled)
{
    public static StoreAppDto Create(StoreApp source) => new(
        source.Definition.Key.ToString(),
        source.Definition.DisplayName,
        source.IsInstalled);
}
