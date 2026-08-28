namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// What an app is, as far as the platform is concerned. Every phone can run every entry here —
/// there are no models and no capability tiers, so what a catalog entry costs is the work of adding
/// it, not a rebalance of who may install it.
/// </summary>
public sealed record AppDefinition(AppKey Key, string DisplayName);

/// <summary>
/// The backend owns the app list so adding or rebalancing an app needs no mod redeploy — the same
/// reasoning the Skills module's SkillCatalog applies to its action-to-XP map.
/// </summary>
public static class AppCatalog
{
    public static readonly IReadOnlyDictionary<AppKey, AppDefinition> Entries = new Dictionary<AppKey, AppDefinition>
    {
        [AppKey.Messages] = new(AppKey.Messages, "Messages"),
        [AppKey.Contacts] = new(AppKey.Contacts, "Contacts"),
    };

    public static bool Contains(AppKey key) => Entries.ContainsKey(key);

    public static AppDefinition Get(AppKey key) =>
        Entries.TryGetValue(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No catalog entry for app '{key}'.");
}
