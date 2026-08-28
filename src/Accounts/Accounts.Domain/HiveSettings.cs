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

    /// <summary>Messages one phone may send per minute. A phone is a number, so this is per number.</summary>
    public int SmsPerMinutePerPhone { get; set; } = 20;

    public int SmsMaxBodyLength { get; set; } = 480;

    /// <summary>
    /// The three caps below used to be per-handset capability numbers on a <c>PhoneModel</c> catalog
    /// row — what made a burner a burner. Nothing could ever pick which model a player got, so the
    /// tier was enforced everywhere and chosen by nobody; they are hive-wide knobs now, and every
    /// phone gets the same ones.
    /// </summary>
    public int PhoneContactLimit { get; set; } = 50;

    /// <summary>
    /// Applied when a message is appended, and carried on the resulting event so a replay rebuilds
    /// the history that existed. Lowering it therefore trims each thread on its next message rather
    /// than at once.
    /// </summary>
    public int PhoneThreadMessageLimit { get; set; } = 30;

    /// <summary>Recipients on one message, excluding the sender. Below 2 there are no groups.</summary>
    public int PhoneMaxGroupParticipants { get; set; } = 5;
}
