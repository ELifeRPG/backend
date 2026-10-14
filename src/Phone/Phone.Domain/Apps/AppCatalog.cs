namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// What an app is, as far as the platform is concerned. Every phone can run every entry here —
/// there are no models and no capability tiers, so what a catalog entry costs is the work of adding
/// it, not a rebalance of who may install it.
///
/// <paramref name="IsSystemApp"/> is the one thing that is not uniform: a system app is the phone's
/// own software rather than something a player chose. It ships on every new phone, it is refused by
/// <c>PhoneDevice.UninstallApp</c>, and the store never offers it — deleting the dialer, the
/// settings or the store itself would leave a handset nobody could repair from inside the game.
/// Installing one is still allowed, because that is the only route by which a phone provisioned
/// before an app existed ever receives it.
/// </summary>
public sealed record AppDefinition(AppKey Key, string DisplayName, bool IsSystemApp);

/// <summary>
/// The backend owns the app list so adding or rebalancing an app needs no mod redeploy — the same
/// reasoning the Skills module's SkillCatalog applies to its action-to-XP map.
/// </summary>
public static class AppCatalog
{
    /// <summary>
    /// Declaration order is iteration order for <see cref="SystemApps"/> and <see cref="StoreApps"/>,
    /// which is what a new phone installs in and what <c>GET /apps</c> returns — so it is kept in
    /// step with <see cref="AppKey"/>'s ordinals rather than left to chance.
    /// </summary>
    public static readonly IReadOnlyDictionary<AppKey, AppDefinition> Entries = new Dictionary<AppKey, AppDefinition>
    {
        [AppKey.Settings] = new(AppKey.Settings, "Settings", IsSystemApp: true),
        [AppKey.Store] = new(AppKey.Store, "App Store", IsSystemApp: true),
        [AppKey.Phone] = new(AppKey.Phone, "Phone", IsSystemApp: true),
        [AppKey.Messages] = new(AppKey.Messages, "Messages", IsSystemApp: true),
        [AppKey.Contacts] = new(AppKey.Contacts, "Contacts", IsSystemApp: true),
        [AppKey.Bank] = new(AppKey.Bank, "Bank", IsSystemApp: false),
        [AppKey.Map] = new(AppKey.Map, "Map", IsSystemApp: false),
    };

    public static IEnumerable<AppDefinition> SystemApps => Entries.Values.Where(entry => entry.IsSystemApp);

    public static IEnumerable<AppDefinition> StoreApps => Entries.Values.Where(entry => !entry.IsSystemApp);

    public static bool Contains(AppKey key) => Entries.ContainsKey(key);

    /// <summary>
    /// Deliberately total where <see cref="Get"/> throws: the uninstall path consults this, and it is
    /// reachable with an off-catalog key from a direct command send. An app that does not exist is
    /// not a system app, which is the answer that lets that case fall through to "not installed".
    /// </summary>
    public static bool IsSystemApp(AppKey key) =>
        Entries.TryGetValue(key, out var definition) && definition.IsSystemApp;

    public static AppDefinition Get(AppKey key) =>
        Entries.TryGetValue(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No catalog entry for app '{key}'.");
}
