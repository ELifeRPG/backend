namespace ELifeRPG.World.Domain;

/// <summary>
/// Deployment-wide, admin-tunable World/inventory settings — same precedent as
/// <c>ELifeRPG.Accounts.Domain.HiveSettings</c>: a plain singleton document rather than an aggregate,
/// because there is no history worth replaying here, only a current value per knob.
///
/// Holds only the <b>operationally tunable</b> numbers. The three <b>structural</b> caps — container
/// depth, attribute key count, attribute value length — are domain constants
/// (<see cref="Items.ItemInstance.MaxContainerDepth"/>, <see cref="Items.ItemAttributes.MaxKeys"/>,
/// <see cref="Items.ItemAttributes.MaxValueLength"/>), not fields here — see the phase 1 task brief's
/// Controller ruling. A structural cap is an invariant already baked into stored data, so making it
/// runtime-tunable would let a settings edit retroactively invalidate rows that were valid when
/// written.
///
/// Every setting carries a property initializer, and that is load-bearing — same reasoning as
/// <c>HiveSettings</c>: System.Text.Json leaves an absent property at its initialized value, so a
/// document written before a knob existed reads back with the intended default rather than with
/// zero. A zero <see cref="MaxInstancesPerGrant"/> would mean "no grant may ever mint anything",
/// which is not a default anyone would choose on purpose.
/// </summary>
public sealed class WorldSettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; init; } = SingletonId;

    /// <summary>Caps how many discrete entities a single grant call may mint. See ItemInstanceRepository.GrantAsync.</summary>
    public int MaxInstancesPerGrant { get; set; } = 100;

    /// <summary>Ground TTL, in seconds, for a despawning item — see <see cref="Items.ItemInstance.MoveToWorld"/>.</summary>
    public int GroundItemTtlSeconds { get; set; } = 3600;

    /// <summary>Default and max page size for the pending-delivery read (task 4).</summary>
    public int MaxPendingPageSize { get; set; } = 50;

    /// <summary>How many times a pending row may be served before it is parked as undeliverable (task 5).</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>
    /// Caps how many instances one <c>POST /api/inventory/acks</c> request may acknowledge. The design
    /// spec enforces batch size as a <b>count</b>, not a body size, and publishes it through
    /// <c>GET /api/inventory/limits</c> so the Bridge chunks correctly rather than discovering the cap
    /// as a rejection. An over-sized batch is <c>batch_too_large</c> (400, not retryable — chunk and
    /// resend); the cap doubles as a lock-duration cap on the ack transaction.
    /// </summary>
    public int MaxAcksPerBatch { get; set; } = 100;

    /// <summary>
    /// Caps how many engine-spawned children a single ack entry may declare — a rifle's magazine, a
    /// phone's SIM. Same <c>batch_too_large</c> rejection and same publishing through the limits
    /// endpoint as <see cref="MaxAcksPerBatch"/>. Distinct from it because the two bound different
    /// things: the batch cap bounds how many parents one request touches, this one bounds the mint
    /// fan-out under any single parent.
    /// </summary>
    public int MaxChildrenPerAck { get; set; } = 32;
}
