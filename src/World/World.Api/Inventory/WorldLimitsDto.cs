using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>
/// Composes both halves of the phase 1 plan's Controller ruling into the one document the Bridge
/// reads at boot: the operationally tunable <see cref="WorldSettings"/> values, alongside the
/// structural domain constants (<see cref="ItemInstance.MaxContainerDepth"/>,
/// <see cref="ItemAttributes.MaxKeys"/>, <see cref="ItemAttributes.MaxKeyLength"/>,
/// <see cref="ItemAttributes.MaxValueLength"/>, <see cref="ScopeCursor.MaxSequence"/>) that never
/// change at runtime. The Bridge hardcodes nothing from either half.
///
/// <b>Completeness is the contract, not a nicety.</b> docs/bridge.md tells an integrator this endpoint
/// publishes <i>every</i> cap the write path enforces and that nothing on it should ever appear as a
/// literal in mod code. <c>WorldSettingsTests.WorldLimitsDto_PublishesEveryTunableKnobAndStructuralCap</c>
/// holds that claim true mechanically: it walks <see cref="WorldSettings"/>' own properties and this
/// module's structural constants and fails if any of them is missing here. It was added because two had
/// already gone missing — <see cref="ItemAttributes.MaxKeyLength"/> (enforced, and rejected as
/// <c>AttributeLimit</c>, but unpublished) and <see cref="ScopeCursor.MaxSequence"/> (enforced as
/// <c>sequence_out_of_range</c>, and written into docs/bridge.md as the literal the doc forbids).
/// </summary>
public sealed record WorldLimitsDto(
    int MaxInstancesPerGrant,
    int MaxContainerDepth,
    int MaxAttributeKeys,
    int MaxAttributeKeyLength,
    int MaxAttributeValueLength,
    int GroundItemTtlSeconds,
    int MaxPendingPageSize,
    int MaxDeliveryAttempts,
    int MaxAcksPerBatch,
    int MaxChildrenPerAck,
    int MaxUpsertsPerBatch,
    int MaxDeletesPerBatch,
    int BatchIdRetentionSeconds,
    int SuspiciousReconcileScopeRowsThreshold,
    int SuspiciousReconcileUpsertsThreshold,
    int SuspiciousReconcileSweptPercentThreshold,
    int MaxUnknownPrefabSightingsPerBatch,
    int MaxPrefabClassNameLength,
    int MaxSampleContextLength,
    int MaxCountPerSighting,
    int MaxUnknownPrefabQueryPageSize,
    int MaxUnknownPrefabQueryOffset,
    long MaxSequence,
    int SnapshotRequestBurst,
    int SnapshotRequestsPerMinute,
    int UnknownPrefabRequestBurst,
    int UnknownPrefabRequestsPerMinute)
{
    public static WorldLimitsDto Create(WorldSettings settings) => new(
        settings.MaxInstancesPerGrant,
        ItemInstance.MaxContainerDepth,
        ItemAttributes.MaxKeys,
        // Whole-branch review, M4: enforced since phase 1 (ItemAttributes.Validate) and rejected as
        // AttributeLimit, but never published — the exact shape of unpublished cap this endpoint exists
        // to make impossible.
        ItemAttributes.MaxKeyLength,
        ItemAttributes.MaxValueLength,
        settings.GroundItemTtlSeconds,
        settings.MaxPendingPageSize,
        settings.MaxDeliveryAttempts,
        settings.MaxAcksPerBatch,
        settings.MaxChildrenPerAck,
        settings.MaxUpsertsPerBatch,
        settings.MaxDeletesPerBatch,
        settings.BatchIdRetentionSeconds,
        // Task 4: published alongside the other snapshot caps for the same reason they are — so the
        // Bridge knows, before it ever sends one, the shape of Full batch this deployment will refuse
        // as suspicious_reconcile rather than discovering it as a 422 it cannot retry its way out of.
        settings.SuspiciousReconcileScopeRowsThreshold,
        settings.SuspiciousReconcileUpsertsThreshold,
        settings.SuspiciousReconcileSweptPercentThreshold,
        // Task 5: published so the Bridge chunks POST /api/inventory/unknown-prefabs correctly and
        // knows the structural string/count bounds before it ever hits one as a rejection.
        settings.MaxUnknownPrefabSightingsPerBatch,
        UnknownPrefabSighting.MaxPrefabClassNameLength,
        UnknownPrefabSighting.MaxSampleContextLength,
        UnknownPrefabSighting.MaxCountPerSighting,
        settings.MaxUnknownPrefabQueryPageSize,
        // Review round 2: offset is clamped, not just floored, so staff/tooling know the ceiling before
        // hitting it as a silently-truncated result rather than a rejection.
        settings.MaxUnknownPrefabQueryOffset,
        // Whole-branch review: the same class of gap as MaxAttributeKeyLength above. A Full batch's
        // `sequence` ceiling is enforced (sequence_out_of_range, 400) and docs/bridge.md had to write
        // it out as a literal because there was nowhere to read it from. A domain constant rather than
        // a WorldSettings knob — a sanity rail, not a tuning parameter, see ScopeCursor.MaxSequence —
        // which is why it sits with the structural half and not the tunable one.
        ScopeCursor.MaxSequence,
        // Task 6: the two rate-limiting buckets, DERIVED from the same objects WorldModule hands to the
        // rate limiter rather than restated beside them — the published figure cannot drift from the
        // enforced one, which is the entire point of this endpoint existing. Not WorldSettings knobs;
        // see InventoryRateLimits' own doc comment for why a per-request settings read would invert the
        // failure mode this mechanism exists for.
        SnapshotBucket.TokenLimit,
        InventoryRateLimits.RequestsPerMinute(SnapshotBucket),
        UnknownPrefabBucket.TokenLimit,
        InventoryRateLimits.RequestsPerMinute(UnknownPrefabBucket));

    private static readonly System.Threading.RateLimiting.TokenBucketRateLimiterOptions SnapshotBucket = InventoryRateLimits.Snapshots();

    private static readonly System.Threading.RateLimiting.TokenBucketRateLimiterOptions UnknownPrefabBucket = InventoryRateLimits.UnknownPrefabReports();
}

/// <summary>
/// <c>PATCH /api/inventory/limits</c>'s request body: the <b>tunable</b> half of
/// <see cref="WorldLimitsDto"/> and nothing else. The structural constants and the two rate-limit
/// buckets are deliberately absent — the first are invariants already baked into stored data (see
/// <see cref="WorldSettings"/>' class summary on why making them runtime-tunable would let an edit
/// retroactively invalidate rows that were valid when written), and the second are derived from the
/// same objects the rate limiter itself is built from, so a settable copy could only ever drift from
/// what is enforced.
///
/// Every field is nullable and an omitted one leaves the stored value unchanged — the
/// <c>UpdateHiveSettingsRequestDto</c> precedent's shape exactly. Values are range-checked rather than
/// clamped; see <see cref="UpdateWorldSettingsCommand"/> for the bounds and why each one is where it is.
/// </summary>
public sealed record UpdateWorldLimitsRequestDto(
    // Every parameter carries an explicit `= null` default, and that is not decoration: without it the
    // generated schema lists all fifteen under `required`, so a Kiota client would demand a caller send
    // every knob (as an explicit null) on a request whose entire contract is "send only what you are
    // changing". The default is what makes the published document say what this endpoint actually does.
    int? MaxInstancesPerGrant = null,
    int? GroundItemTtlSeconds = null,
    int? MaxPendingPageSize = null,
    int? MaxDeliveryAttempts = null,
    int? MaxAcksPerBatch = null,
    int? MaxChildrenPerAck = null,
    int? MaxUpsertsPerBatch = null,
    int? MaxDeletesPerBatch = null,
    int? BatchIdRetentionSeconds = null,
    int? SuspiciousReconcileScopeRowsThreshold = null,
    int? SuspiciousReconcileUpsertsThreshold = null,
    int? SuspiciousReconcileSweptPercentThreshold = null,
    int? MaxUnknownPrefabSightingsPerBatch = null,
    int? MaxUnknownPrefabQueryPageSize = null,
    int? MaxUnknownPrefabQueryOffset = null)
{
    public UpdateWorldSettingsCommand ToCommand() => new(
        MaxInstancesPerGrant,
        GroundItemTtlSeconds,
        MaxPendingPageSize,
        MaxDeliveryAttempts,
        MaxAcksPerBatch,
        MaxChildrenPerAck,
        MaxUpsertsPerBatch,
        MaxDeletesPerBatch,
        BatchIdRetentionSeconds,
        SuspiciousReconcileScopeRowsThreshold,
        SuspiciousReconcileUpsertsThreshold,
        SuspiciousReconcileSweptPercentThreshold,
        MaxUnknownPrefabSightingsPerBatch,
        MaxUnknownPrefabQueryPageSize,
        MaxUnknownPrefabQueryOffset);
}
