namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// What an app is, as far as the platform is concerned. <see cref="RequiresSim"/> is the field
/// <c>PhoneAccessPolicy</c> reads to decide whether the SIM checks apply — a later app that keeps
/// its state on the device rather than the SIM (a camera, say) sets it false.
/// </summary>
public sealed record AppDefinition(AppKey Key, string DisplayName, bool RequiresSim);

/// <summary>
/// The backend owns the app list so adding or rebalancing an app needs no mod redeploy — the same
/// reasoning the Skills module's SkillCatalog applies to its action-to-XP map.
/// </summary>
public static class AppCatalog
{
    public static readonly IReadOnlyDictionary<AppKey, AppDefinition> Entries = new Dictionary<AppKey, AppDefinition>
    {
        [AppKey.Messages] = new(AppKey.Messages, "Messages", RequiresSim: true),
        [AppKey.Contacts] = new(AppKey.Contacts, "Contacts", RequiresSim: true),
    };

    public static bool Contains(AppKey key) => Entries.ContainsKey(key);

    public static AppDefinition Get(AppKey key) =>
        Entries.TryGetValue(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No catalog entry for app '{key}'.");
}
