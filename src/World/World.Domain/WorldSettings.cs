namespace ELifeRPG.World.Domain;

/// <summary>
/// Deployment-wide, admin-tunable World/inventory settings — same precedent as
/// <c>ELifeRPG.Accounts.Domain.HiveSettings</c>: a plain singleton document rather than an aggregate,
/// because there is no history worth replaying here, only a current value per knob.
///
/// Holds only the <b>operationally tunable</b> numbers. The three <b>structural</b> caps — container
/// depth, attribute key count, attribute value length — are domain constants
/// (<see cref="Items.ItemInstance.MaxContainerDepth"/>, <see cref="Items.ItemAttributes.MaxKeys"/>,
/// <see cref="Items.ItemAttributes.MaxKeyLength"/>, <see cref="Items.ItemAttributes.MaxValueLength"/>,
/// <see cref="Snapshots.ScopeCursor.MaxSequence"/>) are domain constants, not fields here — see the
/// phase 1 task brief's Controller ruling. A structural cap is an invariant already baked into stored data, so making it
/// runtime-tunable would let a settings edit retroactively invalidate rows that were valid when
/// written.
///
/// Every setting carries a property initializer, and that is load-bearing — same reasoning as
/// <c>HiveSettings</c>: System.Text.Json leaves an absent property at its initialized value, so a
/// document written before a knob existed reads back with the intended default rather than with
/// zero. A zero <see cref="MaxInstancesPerGrant"/> would mean "no grant may ever mint anything",
/// which is not a default anyone would choose on purpose.
///
/// <b>These are genuinely tunable, and that is a recent fact.</b> Every value here is settable through
/// <c>PATCH /api/inventory/limits</c> (<c>World.Application.Settings.UpdateWorldSettingsCommand</c>),
/// range-checked against that handler's bounds table. Until the phase 2 whole-branch review there was
/// no write path at all — the repository exposed only a read and the table held zero rows — while three
/// of the thresholds below had been accepted on the explicit grounds that they were retunable. If a new
/// knob is added here, the completeness test in <c>WorldSettingsTests</c> fails until it is both
/// publishable (<c>WorldLimitsDto</c>) and settable (<c>UpdateWorldSettingsCommand</c>, with a bound).
///
/// <b>A note on the task numbers below.</b> This file was written across two phases whose task numbers
/// both start at 1, so each reference names its phase: "phase 1 task 4" is the pending-delivery read,
/// "phase 2 task 4" is the empty-payload reconcile guard, and so on.
/// </summary>
public sealed class WorldSettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; init; } = SingletonId;

    /// <summary>
    /// Caps how many discrete entities a single grant call may mint. Enforced by each <i>caller</i>
    /// before it opens a transaction — <c>GrantItemsHandler</c>, <c>GatherHandler</c> and
    /// <c>PurchaseListingHandler</c> all check it and return their own "quantity exceeds cap" result —
    /// deliberately <b>not</b> by <c>IItemInstanceRepository.GrantAsync</c>, which takes the count as
    /// already-validated (see its own doc comment). The point of checking early is that an over-sized
    /// grant costs nothing: no transaction, no payment moved, no XP awarded.
    /// </summary>
    public int MaxInstancesPerGrant { get; set; } = 100;

    /// <summary>
    /// Ground TTL, in seconds, for a despawning item: how long a dropped instance survives before the
    /// expiry filter stops returning it. Applied by <c>ApplySnapshotHandler</c>'s root resolver, which
    /// is what stamps <c>ExpiresAt</c> on a World-parented row and on everything nested inside it — not
    /// by <see cref="Items.ItemInstance.MoveToWorld"/>, which this used to point at and which the
    /// snapshot write path does not call (see that method's own note). Only items the catalog classifies
    /// as despawning get a TTL at all; a persistent one gets <c>null</c> and is never swept.
    /// </summary>
    public int GroundItemTtlSeconds { get; set; } = 3600;

    /// <summary>Default and max page size for the pending-delivery read (phase 1 task 4).</summary>
    public int MaxPendingPageSize { get; set; } = 50;

    /// <summary>How many times a pending row may be served before it is parked as undeliverable (phase 1 task 5).</summary>
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
    /// radio's battery. Same <c>batch_too_large</c> rejection and same publishing through the limits
    /// endpoint as <see cref="MaxAcksPerBatch"/>. Distinct from it because the two bound different
    /// things: the batch cap bounds how many parents one request touches, this one bounds the mint
    /// fan-out under any single parent.
    /// </summary>
    public int MaxChildrenPerAck { get; set; } = 32;

    /// <summary>
    /// Caps how many entries one <c>POST /api/inventory/snapshots</c> batch's <c>upserts</c> array may
    /// carry. Same shape as <see cref="MaxAcksPerBatch"/>: enforced as a <b>count</b>, not a body size,
    /// checked before a single row is read (the batch caps double as lock-duration caps on the one
    /// transaction phase 2 task 2's write path opens), rejected whole as <c>batch_too_large</c> (400,
    /// not retryable — chunk and resend), and published on <c>GET /api/inventory/limits</c> so the
    /// Bridge chunks correctly.
    /// </summary>
    public int MaxUpsertsPerBatch { get; set; } = 1000;

    /// <summary>Same reasoning as <see cref="MaxUpsertsPerBatch"/>, over a snapshot batch's <c>deletes</c> array — a distinct cap because the two arrays bound different things.</summary>
    public int MaxDeletesPerBatch { get; set; } = 1000;

    /// <summary>
    /// How long a <c>POST /api/inventory/snapshots</c> batch's <c>batchId</c> is remembered for replay
    /// detection (phase 2 task 3) — 24 hours, comfortably longer than any realistic store-and-forward buffer
    /// delay. A replay within this window returns the original stored response verbatim, with
    /// <c>replayOfPriorBatch: true</c>. Outside it, a replay is no longer looked up and is instead
    /// re-applied fresh — safe on its own merits, since per-instance revision last-write-wins already
    /// makes re-sending the same content a no-op; the retention window only trades a little storage for
    /// giving the Bridge back the exact original counts instead of a set of zeros. Published on
    /// <c>GET /api/inventory/limits</c> alongside the other snapshot caps so the Bridge never hardcodes
    /// how long its own buffer may safely hold a batch before a resend stops being treated as "the same
    /// batch".
    /// </summary>
    public int BatchIdRetentionSeconds { get; set; } = 86400;

    /// <summary>
    /// Phase 2 task 4, the empty-payload guard's gate: the guard only ever considers a
    /// <see cref="Snapshots.SnapshotMode.Full"/> reconcile whose scope holds more than this many
    /// <b>sweep-eligible</b> rows — live, and neither <c>PendingSpawn</c> nor <c>RemovedByStaff</c>,
    /// since the sweep can never touch either and rows that were never at risk must not count toward how
    /// much was at stake. Such a batch is then refused when it also fails one of the two evidence tests —
    /// <see cref="SuspiciousReconcileUpsertsThreshold"/> (a near-empty payload) or
    /// <see cref="SuspiciousReconcileSweptPercentThreshold"/> (a sweep accounting for essentially the
    /// whole scope). A refusal is whole: <c>422 suspicious_reconcile</c>, recorded as a
    /// <see cref="Snapshots.SuspiciousReconcile"/> for staff instead of being applied.
    ///
    /// This is a gate on both arms, never a test on its own — a large sweep by itself is exactly what an
    /// honest mass-loss reconcile looks like.
    ///
    /// <b>On the name: "ScopeRows", not "EligibleScopeRows", is a deliberate call rather than drift.</b>
    /// Eligible rows genuinely <i>are</i> rows in the scope — a subset of them — so the name is less
    /// precise than it could be but never names a different quantity, which is the line that matters.
    /// (Contrast the predecessor this was renamed from: <c>SweptRowsThreshold</c> gating scope size named
    /// something else entirely, and that had to be fixed.) Against that imprecision sits the cost of
    /// churning a field published on <c>GET /api/inventory/limits</c> a second time in consecutive
    /// commits, which a doc comment can absorb and a Bridge integrator cannot. Controller ruling, review
    /// round 4 — carried on <see cref="Snapshots.SuspiciousReconcile.ScopeRowCount"/> and
    /// <see cref="Snapshots.SuspiciousReconcile.ScopeRowsThreshold"/> too, and deliberately <i>not</i>
    /// extended to the staff-facing <c>422</c> title, which spells out "sweep-eligible rows" in full:
    /// a knob's name can lean on the comment beside it, a sentence a human reads while working out what
    /// happened cannot.
    ///
    /// <b>It measures what was at stake — not the sweep, and not the raw scope either.</b> Neither
    /// substitution is safe, and each took a review round to establish.
    ///
    /// Not the sweep (round 2): the sweep's own protection rules (see
    /// <c>ApplySnapshotHandler.ComputeSweep</c>) exist to save rows from deletion, so gating on the
    /// surviving sweep count would let every row they saved also make the guard quieter about the ones
    /// they didn't — a 46-row character whose mod reported <i>nothing at all</i> swept only the five rows
    /// not sitting inside a protected crate, slipped under the gate, and lost those five with no staff
    /// record. Gating on what was at stake keeps the two concerns independent: protection decides what is
    /// deleted, the guard decides whether a claim this large deserves belief at all.
    ///
    /// Not the raw scope either (round 3): undelivered grants and staff tombstones are live rows the
    /// scope read deliberately returns (the sweep's anchor walk needs them as parents) and that the sweep
    /// then refuses to touch. Counting them over-fired on <i>correct</i> batches — a character with 30
    /// undelivered grants and 2 carried items had an accurate <c>Full</c> naming both carried rows refused
    /// with nothing to sweep at all, and its two legitimate upserts discarded — and it did so
    /// repeatably, since the condition is a property of stored state rather than of the batch, so every
    /// retry was refused identically. It also broke the naked-logout case below permanently for any
    /// character holding 26+ tombstones, tombstones being live rows kept indefinitely.
    ///
    /// <b>Why the gate exists rather than the arms standing alone.</b> "The player logged out naked" is
    /// a real, ordinary scenario: a character who genuinely holds nothing produces a <c>Full</c> batch
    /// with zero upserts that sweeps 100% of their scope, and it must still work. The gate is the only
    /// thing that distinguishes it from the failure this guard is for, since both arms fire on it.
    ///
    /// <b>Why 25.</b> A fully-kitted Reforger character — uniform, vest, backpack, a weapon or two,
    /// magazines, medical — sits in the low tens of rows once container contents are counted, so 25 is
    /// far above the "logged out with a pistol and two mags" case the guard must never touch, and below
    /// the point where a wipe stops being worth a human's attention. The two outcomes this trades
    /// between are deliberately asymmetric: a false trip costs one rejected batch and a stale scope
    /// until the next honest snapshot arrives (nothing is lost, and the <see cref="Snapshots.ScopeCursor"/>
    /// is not advanced, so the corrected reconcile is accepted at the same sequence), while a false
    /// acceptance costs a player their inventory with only the soft-delete retention window standing
    /// between them and losing it for good. That asymmetry is the whole reason this is tunable at all:
    /// the right number is a deployment question — inventory sizes differ per server ruleset — not a
    /// design one, which is why it lives here and is published on <c>GET /api/inventory/limits</c>
    /// rather than being a domain constant like <see cref="Items.ItemInstance.MaxContainerDepth"/>.
    /// </summary>
    public int SuspiciousReconcileScopeRowsThreshold { get; set; } = 25;

    /// <summary>
    /// The guard's second half: the batch must carry <b>fewer than</b> this many upserts for the
    /// refusal to trigger — see <see cref="SuspiciousReconcileScopeRowsThreshold"/> for why both halves
    /// are required. 3 is "near-empty": a mod that has genuinely lost its view of the world reports
    /// nothing, or a stray item or two it happens to still see; a mod that is honestly reporting a
    /// mostly-emptied inventory reports what remains. Distinct from
    /// <see cref="SuspiciousReconcileScopeRowsThreshold"/> rather than derived from it because the two
    /// bound different things — one is about how much is at stake, the other about how little evidence
    /// is being offered for it.
    ///
    /// On its own this arm is trivially disarmed, which is what
    /// <see cref="SuspiciousReconcileSweptPercentThreshold"/> exists to fix: a mod naming three items
    /// clears this test at <i>any</i> sweep size at all, so before that second arm existed a
    /// three-upsert batch could wipe an inventory of unbounded size (review round 1).
    /// </summary>
    public int SuspiciousReconcileUpsertsThreshold { get; set; } = 3;

    /// <summary>
    /// The guard's second, proportional arm, added in review round 1 to close the hole
    /// <see cref="SuspiciousReconcileUpsertsThreshold"/> leaves open. A
    /// <see cref="Snapshots.SnapshotMode.Full"/> reconcile is also refused when the sweep accounts for
    /// <b>at least this percentage</b> of the scope's <b>sweep-eligible</b> rows — live rows that are
    /// neither undelivered grants nor staff-removed — regardless of how many upserts it carried.
    /// The denominator is deliberately not "everything the scope held": review round 3 found that
    /// counting rows the sweep can never touch refused honest batches outright, since a character
    /// holding thirty undelivered grants and two carried items would be measured against thirty-two.
    /// It is the same set <see cref="SuspiciousReconcileScopeRowsThreshold"/> gates on, so the guard
    /// and the sweep measure the same rows and this arm can never exceed 100%. Either arm is enough to refuse, and both are gated behind
    /// <see cref="SuspiciousReconcileScopeRowsThreshold"/> first.
    ///
    /// <b>Why a proportion and not just a bigger upsert count.</b> The absolute arm asks "how many rows
    /// did the mod name", which does not scale: three named items is thin evidence against a 30-row
    /// inventory and absurd evidence against a 300-row one, yet the same constant answers both. The
    /// question that scales is "what share of what should have been there did the mod fail to account
    /// for", and that is this arm.
    ///
    /// <b>Why the row-count gate still applies.</b> Without it this arm would refuse the one scenario
    /// the guard must never touch: a character who genuinely holds a handful of things and now holds
    /// nothing sweeps 100% of their scope by construction. "The player logged out naked" stays an
    /// ordinary, accepted reconcile because a five-row scope never reaches
    /// <see cref="SuspiciousReconcileScopeRowsThreshold"/> in the first place.
    ///
    /// <b>Why 90.</b> It is deliberately close to total: this arm is meant to catch "the mod can barely
    /// see anything", not to second-guess a large but well-evidenced reconcile. A batch that accounts
    /// for even a tenth of a large scope passes it and applies. The same asymmetry that sets the row
    /// threshold applies here — a false refusal costs one batch and a stale scope, a false acceptance
    /// costs a player their inventory — which is again why this is a deployment knob published on
    /// <c>GET /api/inventory/limits</c> rather than a constant.
    /// </summary>
    public int SuspiciousReconcileSweptPercentThreshold { get; set; } = 90;

    /// <summary>
    /// Phase 2 task 5: caps how many entries one <c>POST /api/inventory/unknown-prefabs</c> batch's
    /// <c>sightings</c> array may carry. Same shape as <see cref="MaxUpsertsPerBatch"/>: a <b>count</b>,
    /// not a body size, checked before a single row is touched, rejected whole as <c>batch_too_large</c>
    /// (400, not retryable — chunk and resend), and published on <c>GET /api/inventory/limits</c>.
    /// Distinct from that field rather than reused because this endpoint's rows are far cheaper per
    /// entry (one deterministic-id upsert, no catalog/container/server checks), so it can reasonably
    /// afford a larger default.
    /// </summary>
    public int MaxUnknownPrefabSightingsPerBatch { get; set; } = 1000;

    /// <summary>
    /// Default and max page size for <c>GET /api/inventory/unknown-prefabs</c>' staff promotion queue
    /// (phase 2 task 5) — same clamping shape as <see cref="MaxPendingPageSize"/>: a null or out-of-range
    /// <c>limit</c> falls back to this value, and any larger requested value is clamped down to it,
    /// never up.
    /// </summary>
    public int MaxUnknownPrefabQueryPageSize { get; set; } = 100;

    /// <summary>
    /// Caps how far <c>GET /api/inventory/unknown-prefabs</c>' <c>offset</c> may reach (review round 2:
    /// the first cut floored <c>offset</c> at 0 but left it unbounded above). <c>Skip(offset)</c> costs
    /// Postgres work proportional to <c>offset</c> regardless of how many rows are ultimately returned,
    /// so an unbounded value lets a single request force an arbitrarily large index scan — this endpoint
    /// is staff-authenticated, not untrusted, but "authenticated" and "incapable of a mistaken or
    /// compromised huge value" are not the same property, and every other bound on a caller-controlled
    /// number in this module is enforced regardless of who is allowed to call it. An out-of-range
    /// <c>offset</c> is clamped down to this value, same as <c>limit</c> is clamped down to
    /// <see cref="MaxUnknownPrefabQueryPageSize"/> — silently returning a valid (if empty-past-the-cap)
    /// page rather than rejecting the request, since an offset a deployment later shrinks this value
    /// below is not a caller error.
    /// </summary>
    public int MaxUnknownPrefabQueryOffset { get; set; } = 100_000;
}
