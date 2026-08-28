using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Application.Settings;

/// <summary>
/// Backs <c>PATCH /api/inventory/limits</c>, mirroring
/// <c>Accounts.Application.Hive.UpdateHiveSettingsCommand</c>: every field is nullable and an omitted
/// one leaves the stored value unchanged, so a caller never has to send back knobs it does not care
/// about (and cannot accidentally reset one it did not know existed).
///
/// <b>Why this exists at all.</b> Phase 2 accepted three unmeasured reconcile-guard thresholds —
/// <see cref="WorldSettings.SuspiciousReconcileScopeRowsThreshold"/>,
/// <see cref="WorldSettings.SuspiciousReconcileUpsertsThreshold"/> and
/// <see cref="WorldSettings.SuspiciousReconcileSweptPercentThreshold"/> — on the explicit grounds that
/// they are deployment knobs, retunable against real data. Until this command shipped there was no
/// write path of any kind: <c>IWorldSettingsRepository</c> exposed only a read, the singleton table held
/// zero rows, and all fifteen values were hardcoded defaults. "Tunable" has to be true of something a
/// deployment can actually turn.
///
/// Every field a caller supplies is <b>range-checked, not clamped</b> — same call as the
/// <c>HiveSettings</c> precedent makes, for the same reason: a value outside these bounds is far more
/// likely a typo or a misplaced decimal point than an intention, and silently applying the nearest
/// legal number would leave the operator believing something that is not true. The bounds themselves
/// are the point: tasks 2-5 spent five review rounds bounding every caller-controlled number on this
/// module's write path, and a settings endpoint that could set a batch cap to <c>int.MaxValue</c>
/// would hand all of it back.
/// </summary>
public sealed record UpdateWorldSettingsCommand(
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
    int? MaxUnknownPrefabQueryOffset = null) : IRequest<WorldSettings>;

public sealed class UpdateWorldSettingsHandler(IWorldSettingsRepository repository)
    : IRequestHandler<UpdateWorldSettingsCommand, WorldSettings>
{
    /// <summary>
    /// The bounds, in one table rather than scattered through the assignment block below, so that
    /// "what may this knob be set to" is answerable in one place and the completeness test
    /// (<c>WorldSettingsTests</c>) can assert every knob has an entry.
    ///
    /// Two rules set them, and each bound is one or the other:
    ///
    /// <b>The lower bound is the smallest value at which the mechanism still does its job.</b> Almost
    /// every knob here is a cap on a count, and zero means "nothing may ever happen" — no grant may
    /// mint, no batch may carry an entry, no page may return a row. Those are 1. The two exceptions are
    /// deliberate: <see cref="WorldSettings.SuspiciousReconcileUpsertsThreshold"/> at 0 disables that
    /// one arm of the reconcile guard on purpose (no batch carries fewer than zero upserts) while the
    /// proportional arm keeps standing, and <see cref="WorldSettings.MaxUnknownPrefabQueryOffset"/> at 0
    /// pins the staff queue to its first page, which is a coherent thing to want.
    ///
    /// <b>The upper bound is the point past which the knob stops protecting what it exists to
    /// protect.</b> The batch caps double as lock-duration caps on a single Postgres transaction, so a
    /// cap large enough to hold an arbitrarily long transaction is not a cap. The reconcile-guard
    /// thresholds silently disarm the guard once they sit above any inventory a real character could
    /// hold, or above 100% for the proportional arm. <see cref="WorldSettings.MaxUnknownPrefabQueryOffset"/>
    /// bounds a <c>Skip(offset)</c> whose cost Postgres pays in full regardless of how many rows come
    /// back. And <see cref="WorldSettings.BatchIdRetentionSeconds"/> bounds how long <c>AppliedBatch</c>
    /// rows accumulate before anything prunes them — nothing does yet, which is exactly why the ceiling
    /// is not open-ended.
    ///
    /// All of these are generous against their defaults (mostly one to two orders of magnitude of
    /// headroom). They are not there to second-guess a deployment; they are there so that a mistyped
    /// value is a 400 naming the knob rather than a silently broken invariant discovered weeks later.
    /// </summary>
    private static readonly Dictionary<string, (int Min, int Max)> Bounds = new()
    {
        [nameof(WorldSettings.MaxInstancesPerGrant)] = (1, 1_000),
        // 30 days. Below 1 a dropped item expires the instant it lands; above this, "ground TTL" stops
        // describing anything and ground items are simply permanent.
        [nameof(WorldSettings.GroundItemTtlSeconds)] = (1, 2_592_000),
        [nameof(WorldSettings.MaxPendingPageSize)] = (1, 1_000),
        // 0 would park every grant undeliverable before the delivery loop ever served it once.
        [nameof(WorldSettings.MaxDeliveryAttempts)] = (1, 100),
        [nameof(WorldSettings.MaxAcksPerBatch)] = (1, 10_000),
        [nameof(WorldSettings.MaxChildrenPerAck)] = (1, 1_000),
        [nameof(WorldSettings.MaxUpsertsPerBatch)] = (1, 10_000),
        [nameof(WorldSettings.MaxDeletesPerBatch)] = (1, 10_000),
        // A minute is the shortest window in which replay detection can survive any realistic
        // store-and-forward buffer; 30 days is the longest AppliedBatch may accumulate for while
        // pruning is still phase 3's first item rather than a shipped mechanism.
        [nameof(WorldSettings.BatchIdRetentionSeconds)] = (60, 2_592_000),
        // Above ~10,000 sweep-eligible rows in one character's or container's scope, the gate can never
        // be reached by a real inventory and the whole guard is off.
        [nameof(WorldSettings.SuspiciousReconcileScopeRowsThreshold)] = (1, 10_000),
        // 0 disables this arm deliberately. The ceiling keeps it from exceeding MaxUpsertsPerBatch's own
        // default, past which "fewer than this many upserts" is true of every batch that can be sent.
        [nameof(WorldSettings.SuspiciousReconcileUpsertsThreshold)] = (0, 1_000),
        // A percentage. Above 100 the arm can never fire; at 0 it would fire on a Full that swept
        // nothing at all.
        [nameof(WorldSettings.SuspiciousReconcileSweptPercentThreshold)] = (1, 100),
        [nameof(WorldSettings.MaxUnknownPrefabSightingsPerBatch)] = (1, 10_000),
        [nameof(WorldSettings.MaxUnknownPrefabQueryPageSize)] = (1, 1_000),
        [nameof(WorldSettings.MaxUnknownPrefabQueryOffset)] = (0, 1_000_000),
    };

    /// <summary>Exposed for the completeness test — see <see cref="Bounds"/>.</summary>
    public static IReadOnlyDictionary<string, (int Min, int Max)> SettingBounds => Bounds;

    public async ValueTask<WorldSettings> Handle(UpdateWorldSettingsCommand request, CancellationToken cancellationToken)
    {
        // Read-modify-write through the same single point lookup GET uses. Every knob is validated
        // before anything is stored, so a request naming one good value and one bad one writes nothing
        // rather than half-applying.
        var settings = await repository.GetAsync(cancellationToken);

        if (request.MaxInstancesPerGrant is { } maxInstancesPerGrant)
        {
            settings.MaxInstancesPerGrant = Bounded(nameof(request.MaxInstancesPerGrant), maxInstancesPerGrant);
        }

        if (request.GroundItemTtlSeconds is { } groundItemTtlSeconds)
        {
            settings.GroundItemTtlSeconds = Bounded(nameof(request.GroundItemTtlSeconds), groundItemTtlSeconds);
        }

        if (request.MaxPendingPageSize is { } maxPendingPageSize)
        {
            settings.MaxPendingPageSize = Bounded(nameof(request.MaxPendingPageSize), maxPendingPageSize);
        }

        if (request.MaxDeliveryAttempts is { } maxDeliveryAttempts)
        {
            settings.MaxDeliveryAttempts = Bounded(nameof(request.MaxDeliveryAttempts), maxDeliveryAttempts);
        }

        if (request.MaxAcksPerBatch is { } maxAcksPerBatch)
        {
            settings.MaxAcksPerBatch = Bounded(nameof(request.MaxAcksPerBatch), maxAcksPerBatch);
        }

        if (request.MaxChildrenPerAck is { } maxChildrenPerAck)
        {
            settings.MaxChildrenPerAck = Bounded(nameof(request.MaxChildrenPerAck), maxChildrenPerAck);
        }

        if (request.MaxUpsertsPerBatch is { } maxUpsertsPerBatch)
        {
            settings.MaxUpsertsPerBatch = Bounded(nameof(request.MaxUpsertsPerBatch), maxUpsertsPerBatch);
        }

        if (request.MaxDeletesPerBatch is { } maxDeletesPerBatch)
        {
            settings.MaxDeletesPerBatch = Bounded(nameof(request.MaxDeletesPerBatch), maxDeletesPerBatch);
        }

        if (request.BatchIdRetentionSeconds is { } batchIdRetentionSeconds)
        {
            settings.BatchIdRetentionSeconds = Bounded(nameof(request.BatchIdRetentionSeconds), batchIdRetentionSeconds);
        }

        if (request.SuspiciousReconcileScopeRowsThreshold is { } scopeRowsThreshold)
        {
            settings.SuspiciousReconcileScopeRowsThreshold =
                Bounded(nameof(request.SuspiciousReconcileScopeRowsThreshold), scopeRowsThreshold);
        }

        if (request.SuspiciousReconcileUpsertsThreshold is { } upsertsThreshold)
        {
            settings.SuspiciousReconcileUpsertsThreshold =
                Bounded(nameof(request.SuspiciousReconcileUpsertsThreshold), upsertsThreshold);
        }

        if (request.SuspiciousReconcileSweptPercentThreshold is { } sweptPercentThreshold)
        {
            settings.SuspiciousReconcileSweptPercentThreshold =
                Bounded(nameof(request.SuspiciousReconcileSweptPercentThreshold), sweptPercentThreshold);
        }

        if (request.MaxUnknownPrefabSightingsPerBatch is { } maxSightingsPerBatch)
        {
            settings.MaxUnknownPrefabSightingsPerBatch =
                Bounded(nameof(request.MaxUnknownPrefabSightingsPerBatch), maxSightingsPerBatch);
        }

        if (request.MaxUnknownPrefabQueryPageSize is { } maxQueryPageSize)
        {
            settings.MaxUnknownPrefabQueryPageSize =
                Bounded(nameof(request.MaxUnknownPrefabQueryPageSize), maxQueryPageSize);
        }

        if (request.MaxUnknownPrefabQueryOffset is { } maxQueryOffset)
        {
            settings.MaxUnknownPrefabQueryOffset = Bounded(nameof(request.MaxUnknownPrefabQueryOffset), maxQueryOffset);
        }

        await repository.UpsertAsync(settings, cancellationToken);
        return settings;
    }

    private static int Bounded(string setting, int value)
    {
        // A knob with no entry is a programming error, not a caller error: the completeness test fails
        // the build before this can ever be reached, and reaching it anyway must not silently accept an
        // unbounded value.
        var (min, max) = Bounds[setting];
        if (value < min || value > max)
        {
            throw new WorldSettingOutOfRangeException(setting, value, min, max);
        }

        return value;
    }
}
