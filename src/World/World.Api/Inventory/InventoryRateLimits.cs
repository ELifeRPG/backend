using System.Threading.RateLimiting;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>
/// The two token buckets the World module's write path is bounded by (task 6), and the single place
/// their numbers are written down. <c>WorldModule</c> turns each into a named rate-limiting policy
/// partitioned on the caller's <c>client_id</c> claim; <see cref="WorldLimitsDto"/> publishes the same
/// two buckets on <c>GET /api/inventory/limits</c>, derived from these objects rather than restated,
/// so the number the Bridge reads is by construction the number the host enforces.
///
/// <b>Why a token bucket rather than a fixed or sliding window.</b> A store-and-forward Bridge does not
/// produce a smooth request rate: it is silent while the game is quiet, then flushes a backlog the
/// moment connectivity returns. A fixed window would either be sized for the backlog (and so not bound
/// the steady state at all) or sized for the steady state (and so reject every reconnect). A bucket
/// separates the two questions — <c>TokenLimit</c> is how large a backlog may drain at once,
/// <c>TokensPerPeriod</c>/<c>ReplenishmentPeriod</c> is what a client may sustain forever — and lets
/// both be set honestly.
///
/// <b>Why these are constants and not <c>WorldSettings</c> knobs.</b> The partitioner runs on every
/// request through these endpoints, so reading a Marten-backed settings document from it would put a
/// database round trip in front of the mechanism whose whole job is to survive a client that is
/// hammering the host — the failure mode is exactly backwards. They are still published on
/// <c>GET /api/inventory/limits</c>, so nothing is hardcoded on the Bridge side; a deployment that
/// needs different numbers changes them here and redeploys, the same way it would for
/// <c>ItemInstance.MaxContainerDepth</c>.
///
/// <b>Nothing queues.</b> <c>QueueLimit</c> is 0 on both: a queued request is a held connection and a
/// latency the caller cannot see, and the caller here is a buffering client that would rather be told
/// "not now, come back in N seconds" immediately and keep its own durable copy. An instant 429 with a
/// <c>Retry-After</c> is strictly more useful to it than a slow 200.
/// </summary>
internal static class InventoryRateLimits
{
    /// <summary>
    /// <c>POST /api/inventory/snapshots</c>: 10 requests/second sustained per gameserver, 120 in
    /// reserve. The sustained figure is set against what one batch is already allowed to carry —
    /// <c>WorldSettings.MaxUpsertsPerBatch</c> is 1000, so 10/s is 10,000 reported instances a second
    /// from a single server, an order of magnitude above what a full Reforger server's worth of
    /// characters can actually change.
    ///
    /// <b>The burst is not sized to absorb an outage's backlog, and saying otherwise would be
    /// arithmetic that does not hold.</b> A Bridge flushing one batch per character across 60 players
    /// produces roughly 60 requests a second, so a two-minute outage buffers on the order of 7,000
    /// batches — which drains at the sustained rate, in about twelve minutes, not "instantly because
    /// the bucket holds 120". What the reserve actually buys is that the first seconds of a reconnect
    /// are not throttled at all, which is the window where a backlog is largest and a 429 is most
    /// likely to be mistaken for a permanent failure by a client that is already in a bad state.
    ///
    /// A deployment that finds the drain too slow has two honest levers, and the first is better:
    /// coalesce per-character batches into fewer, larger ones (the cap is 1000 upserts, so 60
    /// characters fit comfortably in one request), or raise <c>TokensPerPeriod</c> here. Growing
    /// <c>TokenLimit</c> alone does not help — it changes how fast the first moments go, not the
    /// steady state the backlog is actually paid off at.
    /// </summary>
    internal static TokenBucketRateLimiterOptions Snapshots() => new()
    {
        TokenLimit = 120,
        TokensPerPeriod = 10,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary>
    /// <c>POST /api/inventory/unknown-prefabs</c>: 2 requests/minute sustained per gameserver, 20 in
    /// reserve — deliberately two orders of magnitude tighter than <see cref="Snapshots"/>, which is
    /// what that endpoint's own description has been asking for since task 5.
    ///
    /// It can afford to be. A sighting report is pure telemetry: it upserts one row per distinct prefab
    /// name, keyed deterministically, so the tenth report of the same name in a minute carries no
    /// information the first did not except a count the ninth could have carried instead. A batch may
    /// hold <c>WorldSettings.MaxUnknownPrefabSightingsPerBatch</c> (1000) sightings, so even the
    /// sustained rate is 2000 distinct prefab names a minute from one server, which is far beyond any
    /// real catalog gap. And the thing being guarded against is specifically a mod bug reporting the
    /// same missing prefab on every entity spawn — a loop that produces well-formed, authorised,
    /// individually-cheap requests indefinitely, which nothing else on this path would stop.
    /// </summary>
    internal static TokenBucketRateLimiterOptions UnknownPrefabReports() => new()
    {
        TokenLimit = 20,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(30),
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary>
    /// The sustained rate a bucket allows, expressed the way a Bridge author sizes a flush interval.
    /// Derived from the bucket rather than written down beside it, so the published figure cannot drift
    /// from the enforced one.
    /// </summary>
    internal static int RequestsPerMinute(TokenBucketRateLimiterOptions bucket)
        => (int)Math.Round(bucket.TokensPerPeriod * (60d / bucket.ReplenishmentPeriod.TotalSeconds));
}
