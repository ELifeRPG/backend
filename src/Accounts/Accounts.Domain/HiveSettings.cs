namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Deployment-wide settings for this hive. A singleton document rather than configuration, because
/// whitelist enablement is admin-editable at runtime today and that must not regress into a
/// redeploy. A plain document rather than an aggregate because there is exactly one setting — if a
/// second appears, promoting this is a contained change.
/// </summary>
public sealed class HiveSettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; init; } = SingletonId;

    public bool WhitelistEnabled { get; set; }
}
