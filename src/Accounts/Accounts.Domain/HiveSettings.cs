namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Deployment-wide settings for this hive. A singleton document rather than configuration, because
/// these are admin-editable at runtime and that must not regress into a redeploy. Still a plain
/// document rather than an aggregate: there is no history worth replaying here, only a current value
/// per knob.
///
/// Every setting carries a property initializer, and that is load-bearing: System.Text.Json leaves
/// an absent property at its initialized value, so a document written before a knob existed reads
/// back with the intended default rather than with zero. A zero SMS limit would mean "no message may
/// ever be sent", which is not a default anyone would choose on purpose.
/// </summary>
public sealed class HiveSettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; init; } = SingletonId;

    public bool WhitelistEnabled { get; set; }

    /// <summary>Messages one SIM may send per minute. Throttling is per number, not per handset.</summary>
    public int SmsPerMinutePerSim { get; set; } = 20;

    public int SmsMaxBodyLength { get; set; } = 480;
}
