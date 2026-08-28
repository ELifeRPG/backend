using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
// ItemPersistence only, reached through ItemCatalogEntry.Persistence — the batched
// ItemCatalogEntriesQuery contract this module is already allowed to consume publishes that enum as
// part of its own result shape, so this is the sanctioned contract's type, not a second dependency on
// Items' internals. Needed here because a world-parented instance's ground TTL is derived from it.
using ELifeRPG.Items.Domain;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>One entry of a snapshot batch's <c>upserts</c> array. See <c>SnapshotUpsertRequestDto</c> for the wire shape this is parsed from.</summary>
public sealed record SnapshotUpsertRequest(
    ItemInstanceId InstanceId,
    long Revision,
    ItemId ItemId,
    ParentKind ParentKind,
    CharacterId? ParentCharacterId,
    string? Slot,
    ItemInstanceId? ParentContainerInstanceId,
    WorldTransform? Transform,
    float? Durability,
    int? Ammo,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>One entry of a snapshot batch's <c>deletes</c> array.</summary>
public sealed record SnapshotDeleteRequest(ItemInstanceId InstanceId, long Revision, DeleteReason Reason);

/// <summary>One rejected instance, paired with why — see <see cref="SnapshotRejectionReason"/>.</summary>
public sealed record SnapshotRejection(ItemInstanceId InstanceId, SnapshotRejectionReason Reason);

/// <summary>
/// Batch-level outcome of <c>POST /api/inventory/snapshots</c>. Per-instance problems are reported
/// per instance (<see cref="Applied.Rejected"/>) and never fail the batch — every other case here is
/// one the design spec (or fix round 1's review) makes a first-class batch-level rejection instead:
/// <see cref="DuplicateInstanceId"/>, <see cref="BatchTooLarge"/>, <see cref="SequenceOutOfRange"/> and
/// <see cref="UnsupportedFullScope"/> at 400 (non-retryable), <see cref="WrongServer"/> and
/// <see cref="StaleSequence"/> at 409 (non-retryable), <see cref="SuspiciousReconcile"/> at 422
/// (non-retryable, task 4), and <see cref="ConcurrentReconcile"/> at 409 — the one <b>retryable</b>
/// case this endpoint has, because unlike every other rejection here it names no fault in the request
/// itself.
/// </summary>
public union ApplySnapshotResult(
    ApplySnapshotResult.Applied,
    ApplySnapshotResult.DuplicateInstanceId,
    ApplySnapshotResult.BatchTooLarge,
    ApplySnapshotResult.WrongServer,
    ApplySnapshotResult.StaleSequence,
    ApplySnapshotResult.SequenceOutOfRange,
    ApplySnapshotResult.ConcurrentReconcile,
    ApplySnapshotResult.UnsupportedFullScope,
    ApplySnapshotResult.SuspiciousReconcile)
{
    /// <summary>
    /// The real counts of what this batch changed in storage.
    /// <paramref name="AppliedCount"/>/<paramref name="SkippedNoOp"/>/<paramref name="Deleted"/> each
    /// describe only entries the caller actually sent, so its own arithmetic closes:
    /// <c>applied + skippedNoOp + rejected-upserts == upserts.Count</c> and
    /// <c>deleted + rejected-deletes == deletes.Count</c>.
    ///
    /// <paramref name="CascadeDeleted"/> is the rest of the truth, kept separate rather than folded
    /// into <paramref name="Deleted"/> so neither number has to compromise: deleting a container
    /// soft-deletes everything nested inside it, and those descendants were never entries in the
    /// request for <paramref name="Deleted"/> to describe — but the caller should be told how many
    /// rows actually went away instead of having to reverse-engineer it.
    ///
    /// <paramref name="Swept"/> is the third and last of those, and it is separate from both for the
    /// same reason they are separate from each other: a <see cref="SnapshotMode.Full"/> reconcile
    /// soft-deletes the scope's live rows that the payload simply <i>didn't mention</i> (task 4), which
    /// is neither an entry the request named nor a descendant of one. Always 0 for a
    /// <see cref="SnapshotMode.Partial"/> batch, which by definition says nothing about what it omits.
    ///
    /// <paramref name="ReplayOfPriorBatch"/> is <c>false</c> the first time this <c>batchId</c> is
    /// applied, and <c>true</c> every time it is returned again from the stored <see cref="Domain.Snapshots.AppliedBatch"/>
    /// record within <c>WorldSettings.BatchIdRetentionSeconds</c> — task 3. Every other field on a
    /// replay is byte-identical to the original response; see <c>ApplySnapshotHandler</c>'s own doc
    /// comment for where that lookup happens.
    /// </summary>
    public record Applied(
        Guid BatchId,
        long? Sequence,
        int AppliedCount,
        int SkippedNoOp,
        int Deleted,
        int CascadeDeleted,
        int Swept,
        IReadOnlyList<SnapshotRejection> Rejected,
        bool ReplayOfPriorBatch);

    /// <summary>The same <c>instanceId</c> named twice in one batch — across <c>upserts</c> and <c>deletes</c> combined, since claiming both for one id in one batch is exactly as incoherent as claiming it twice. Likely entity cloning.</summary>
    public record DuplicateInstanceId(ItemInstanceId InstanceId);

    /// <summary><paramref name="Field"/> names which array exceeded its cap — <c>upserts</c> or <c>deletes</c> — so the Bridge knows which axis to chunk on. Same shape as <c>AcknowledgeSpawnsResult.BatchTooLarge</c>.</summary>
    public record BatchTooLarge(string Field, int Requested, int Max);

    /// <summary>
    /// The batch's own declared <c>scope</c> is not reachable from the calling gameserver — the design
    /// spec's correctness core, mechanism 4 ("an inventory write for a character whose CurrentServerId
    /// is a different gameserver is rejected"). The whole batch is meaningless if its own subject
    /// isn't reachable from here, so this fails the batch rather than reporting every instance
    /// individually.
    ///
    /// Both scope kinds are covered. A <c>Character</c> scope is checked against the live
    /// <c>Character.CurrentServerId</c> through the batched <c>CharactersOnServerQuery</c> contract. A
    /// <c>Container</c> scope is checked against the container row's own stored, denormalised
    /// <c>RootGameServerId</c> — there is no live authority for a container the way there is for a
    /// character, and that field is precisely the denormalisation that answers "where is this thing".
    /// A <c>Container</c>-scoped batch whose container the backend has never issued, or has
    /// tombstoned, also lands here rather than in a batch-level "unknown container" case: from the
    /// batch's perspective the outcomes are identical (its declared subject is not reachable from this
    /// gameserver), which is the same precedent <c>AckOutcome.WrongServer</c> already sets for an
    /// instance whose character can't be found.
    ///
    /// Deliberately carries <b>no</b> <c>actualServerId</c>, and its absence is intended rather than a
    /// gap: the value would tell one gameserver which <i>other</i> gameserver currently holds a
    /// character, which is not information a single server in a hive needs to reject a write.
    /// (Controller ruling, phase 2.)
    /// </summary>
    public record WrongServer;

    /// <summary>
    /// Task 3: a <see cref="SnapshotMode.Full"/> batch whose <c>sequence</c> is not strictly greater
    /// than the scope's own <see cref="Domain.Snapshots.ScopeCursor.LastAppliedSequence"/> — a stale or
    /// replayed reconcile arriving after a newer one already landed. Fails the whole batch, the same
    /// way <see cref="WrongServer"/> does, because a <c>Full</c> batch's entire point is "this is
    /// everything in this scope as of this sequence"; applying it out of order would silently regress
    /// the scope rather than reconcile it. <see cref="LastAppliedSequence"/> is deliberately returned
    /// (unlike <see cref="WrongServer"/>'s withheld <c>actualServerId</c>) because it names only a
    /// counter for a scope this batch's own caller already proved reachable — see
    /// <c>ApplySnapshotHandler</c>'s own doc comment for why the check runs only after that proof, never
    /// before it.
    /// </summary>
    public record StaleSequence(long LastAppliedSequence);

    /// <summary>
    /// Fix round 1, item 1: a <c>Full</c> batch's <c>sequence</c> exceeds
    /// <see cref="Domain.Snapshots.ScopeCursor.MaxSequence"/>. Unlike <c>revision</c> (task 2's own
    /// reasoning: no upper bound, since a poisoned revision damages one instance and self-heals the
    /// moment a higher one arrives), a poisoned <c>sequence</c> is unrecoverable by construction — a
    /// monotonic gate cannot be rewound, so one batch naming <c>long.MaxValue</c> would pin a scope's
    /// cursor at the ceiling forever, permanently denying every future <c>Full</c> reconcile of it. A
    /// batch-level rejection, checked purely in memory before any Postgres touch (including the replay
    /// lookup), the same "absurd values rejected before a row is read" property task 2's scalar bounds
    /// already establish for per-instance fields. <paramref name="Max"/> is always
    /// <see cref="Domain.Snapshots.ScopeCursor.MaxSequence"/> regardless of which direction
    /// <paramref name="Requested"/> violated (fix round 2, item 4 adds the symmetric <c>&lt; 0</c> lower
    /// bound) — the value stays meaningful either way, since it names the one number the whole valid
    /// range is defined relative to.
    /// </summary>
    public record SequenceOutOfRange(long Requested, long Max);

    /// <summary>
    /// Fix round 1, item 7: two <c>Full</c> batches raced the same scope's <see cref="Domain.Snapshots.ScopeCursor"/>
    /// and this one lost — Marten's optimistic concurrency check on that document (enabled for
    /// <c>ScopeCursor</c> alone) rejected the commit. Unlike every other case in this union, the batch
    /// itself was entirely valid; it simply lost a race to an equally valid one, which is exactly what
    /// makes this the one <b>retryable</b> outcome this endpoint has — a plain resend, unmodified, is
    /// the correct Bridge response. See <c>ApplySnapshotHandler</c>'s own doc comment and
    /// <c>ScopeCursorConflictException</c> for the translation this maps from.
    /// </summary>
    public record ConcurrentReconcile;

    /// <summary>
    /// Task 4: a <see cref="SnapshotMode.Full"/> batch whose scope is not a single
    /// <see cref="SnapshotScopeKind.Character"/> or <see cref="SnapshotScopeKind.Container"/> — the
    /// design spec's "scope is Character or Container only in this phase". A server-wide reconcile is a
    /// separate, explicitly-authorised staff operation with a dry run, and it lands with world-structure
    /// state where it is actually needed; it is emphatically not something a gameserver may trigger by
    /// widening a field on an ordinary snapshot batch, because <c>Full</c> now <b>deletes</b> and an
    /// unbounded <c>Full</c> is a whole-deployment wipe.
    ///
    /// Two shapes land here, and they are the same mistake at different resolutions. The obvious one is
    /// a <see cref="SnapshotScopeKind"/> outside the two this phase supports —
    /// <see cref="SnapshotScopeKind"/> declares no server-wide member at all, and the endpoint's own
    /// <c>Enum.IsDefined</c> parse already refuses anything else, so this arm is the second lock: it is
    /// what makes a future third member fail closed rather than being silently interpreted as
    /// "everything". The subtler one is a supported kind whose companion id is <b>missing</b> — a
    /// <c>Full</c> batch that names <c>Character</c> but no <c>characterId</c> has no bounded set of
    /// rows to reconcile, which is precisely what "server-wide" means, however it got that way.
    ///
    /// That second shape is deliberately handled here rather than left to
    /// <see cref="Domain.Snapshots.ScopeCursor.BuildKey"/>'s bare <c>InvalidOperationException</c>, the
    /// way tasks 1-3 left it. That was the right call while a <c>Full</c> batch could only ever advance
    /// a counter; it stops being the right call now that reaching the same point with an unbounded
    /// scope would first have had to decide which rows to delete. A destructive path does not get to
    /// rely on "no valid HTTP request can reach this" — it gets a named, checked-in-memory refusal
    /// before it reads a single row.
    /// </summary>
    public record UnsupportedFullScope(SnapshotScopeKind ScopeKind);

    /// <summary>
    /// Task 4's data-loss guard: a <see cref="SnapshotMode.Full"/> reconcile that would have
    /// wiped a scope holding more than <c>WorldSettings.SuspiciousReconcileScopeRowsThreshold</c> rows
    /// while offering too little evidence for it — either fewer than
    /// <c>WorldSettings.SuspiciousReconcileUpsertsThreshold</c> upserts, or a sweep accounting for at
    /// least <c>WorldSettings.SuspiciousReconcileSweptPercentThreshold</c> percent of that scope. Refused
    /// whole at <b>422</b>, non-retryable, and recorded as a
    /// <see cref="Domain.Snapshots.SuspiciousReconcile"/> document for staff — see that type's own doc
    /// comment for why a refusal has to leave something behind.
    ///
    /// 422 rather than 400 or 409 on purpose: nothing about the request is malformed (400) and nothing
    /// about it lost a race or arrived out of order (409). It is a syntactically and semantically valid
    /// batch whose <i>claim</i> the backend declines to act on, which is exactly what
    /// "unprocessable entity" names.
    ///
    /// Non-retryable because resending the identical payload reproduces the identical refusal — the
    /// guard is a pure function of the batch and the scope's current contents, and the scope's contents
    /// are unchanged precisely because this batch was refused. The <see cref="Domain.Snapshots.ScopeCursor"/>
    /// is not advanced either, so once a human has looked, the corrected reconcile is still accepted at
    /// this same sequence.
    ///
    /// Carries every number of the comparison rather than a bare "no": the Bridge logs why it was
    /// refused without a staff round trip, and — unlike <see cref="WrongServer"/>'s withheld
    /// <c>actualServerId</c> — none of them names anything outside the scope this caller has already
    /// been proven entitled to describe. <paramref name="WouldHaveSwept"/> and
    /// <paramref name="ScopeRowCount"/> both count rows the caller's own scope holds and just failed to
    /// report.
    ///
    /// <paramref name="ScopeRowCount"/> counts only rows that were <b>at stake</b> — live, not
    /// still-pending, not staff-tombstoned — which is exactly the set the sweep itself is willing to
    /// touch. It is what <paramref name="ScopeRowsThreshold"/> was compared against and what
    /// <paramref name="SweptPercentThreshold"/> divides by, so the Bridge can reproduce the whole
    /// decision from these six numbers alone. Both names keep "ScopeRow" while meaning that eligible
    /// subset — a deliberate ruling, since a subset is imprecise rather than wrong and these names are
    /// published; see <c>WorldSettings.SuspiciousReconcileScopeRowsThreshold</c>. The endpoint's own
    /// <c>422</c> title does not rely on it and says "sweep-eligible rows" outright.
    /// </summary>
    public record SuspiciousReconcile(
        int WouldHaveSwept,
        int ScopeRowCount,
        int Upserts,
        int ScopeRowsThreshold,
        int UpsertsThreshold,
        int SweptPercentThreshold);
}

/// <summary>Backs <c>POST /api/inventory/snapshots</c>.</summary>
public sealed record ApplySnapshotCommand(
    GameServerId GameServerId,
    Guid BatchId,
    SnapshotScopeKind ScopeKind,
    CharacterId? ScopeCharacterId,
    ItemInstanceId? ScopeContainerInstanceId,
    long? Sequence,
    SnapshotMode Mode,
    IReadOnlyList<SnapshotUpsertRequest> Upserts,
    IReadOnlyList<SnapshotDeleteRequest> Deletes) : IRequest<ApplySnapshotResult>;

/// <summary>
/// Validates a snapshot batch wholesale, then applies it: one load round trip, a revision
/// last-write-wins diff in memory, and one <c>SaveChangesAsync</c> for the whole batch — one Postgres
/// transaction, all-or-nothing, which is what keeps the Bridge's retry story simple. No row locks:
/// revision LWW <i>is</i> the conflict resolution here, and the batch transaction gives atomicity.
///
/// Validation order is deliberate — cheap, in-memory checks first, so a malformed batch never touches
/// Postgres or another module: (1) duplicate <c>instanceId</c>, (2) the <c>Full</c>-mode sequence
/// sanity ceiling, (2b) the <c>Full</c>-mode bounded-scope check (task 4 — a server-wide reconcile is a
/// separate staff operation, see <see cref="ApplySnapshotResult.UnsupportedFullScope"/>),
/// (3) the batch-level idempotency lookup (task 3), (4) count caps, (5) scalar bounds,
/// (6) attribute caps, (7) the in-batch container cycle/depth guard, (8) one batched catalog check,
/// (9) one batched server guard for characters (and, for a <c>Full</c> batch on that scope, the
/// sequence gate — task 3). Steps 1-4 and the two sequence gates are batch-level (fail the whole
/// request); 5-9 are per-instance (reported in <c>Rejected</c>, the rest of the batch still proceeds)
/// except for the scope-character case folded into step 9 — see
/// <see cref="ApplySnapshotResult.WrongServer"/>. Within the per-instance steps the <i>first</i>
/// applicable reason wins: every later step skips an instance already rejected, so a pricier check
/// never re-evaluates (or overwrites the verdict of) one a cheaper check already settled.
///
/// <b>Step 2, the sequence sanity ceiling, sits before step 3's replay lookup — the earliest possible
/// point, right after step 1's purely in-memory duplicate check.</b> Fix round 1, item 1: it needs
/// nothing from Postgres, so it costs a duplicate-id batch nothing extra, and it closes a permanent
/// denial-of-service a poisoned <c>sequence</c> would otherwise inflict on a whole scope forever (a
/// monotonic gate cannot be rewound) — see <see cref="ApplySnapshotResult.SequenceOutOfRange"/>.
///
/// <b>Step 3, the replay lookup, deliberately sits after steps 1-2 rather than before them.</b> Both
/// are pure in-memory and already return before touching Postgres, so putting the replay lookup ahead
/// of either would cost a batch that fails one of them a Postgres round trip it never needed before
/// task 3. Putting it after them costs nothing extra for those cases and still detects a replay before
/// any of the more expensive work below — including the load-and-diff — ever redoes it. It reads the
/// same <see cref="WorldSettings"/> singleton step 4 already needs (for <c>BatchIdRetentionSeconds</c>),
/// so the two are fetched together rather than each threatening its own point where a malformed batch
/// might see the store. A hit returns the stored <c>AppliedBatch</c>'s response verbatim (only
/// <c>replayOfPriorBatch</c> flips true); nothing else in this handler runs.
///
/// <b>The replay lookup key is <see cref="AppliedBatch.BuildKey"/>'s composite of <c>GameServerId</c>
/// and <c>batchId</c>, not the raw <c>batchId</c> alone.</b> Fix round 1, item 3 first closed the read
/// side of this with a separate equality check (a <c>batchId</c> match recorded under a different
/// gameserver was treated as a miss) — without it, a server that merely learns another server's
/// <c>batchId</c> could read back that batch's entire stored body: every <c>instanceId</c> and
/// rejection reason for a scope it was never shown to be allowed to ask about, the same class of
/// cross-tenant leak <see cref="ApplySnapshotResult.WrongServer"/> already refuses to enable by
/// withholding <c>actualServerId</c>. Fix round 2, item 3 found that check alone left the <i>write</i>
/// side open — a plain check on read does nothing to stop a different server's own valid batch,
/// carrying the same <c>batchId</c> value, from overwriting the first server's stored record outright
/// — so the equality check became the composite key itself: two different gameservers can no longer
/// collide on the same row in the first place, whichever order they write in, while a genuine retry
/// (same server, same <c>batchId</c>) still resolves to the same key it always did.
///
/// Only <see cref="ApplySnapshotResult.Applied"/> is ever recorded for replay. Every other batch-level
/// case never mutates storage, so recording one would only add a Postgres <i>write</i> to a path whose
/// entire point is staying out of Postgres for a request that changes nothing — and replaying one costs
/// nothing more than recomputing it, since each is a deterministic function of the request (or of state
/// the handler already has to re-read to answer the question at all).
///
/// Step 10 is the load-and-diff, and it is the first step that reads a stored <c>ItemInstance</c> row.
/// It contributes four more per-instance reasons that all structurally require that read
/// (<see cref="SnapshotRejectionReason.UnknownInstance"/>,
/// <see cref="SnapshotRejectionReason.RemovedByStaff"/>,
/// <see cref="SnapshotRejectionReason.IdentityConflict"/>,
/// <see cref="SnapshotRejectionReason.StaleRevision"/>), completes the two guards task 1 could only
/// half-check (the non-character server guard, and the cycle/depth walk once stored parent edges are
/// merged in), settles the <c>Full</c>-mode sequence gate for a <c>Container</c> scope (task 3, the
/// counterpart to the <c>Character</c>-scope half settled in step 9), and then writes — including, for
/// a <c>Full</c> batch, advancing the scope's <c>ScopeCursor</c>. That advance is optimistic-concurrency
/// checked (fix round 1, item 7): if a second <c>Full</c> batch for the same scope committed first, the
/// resulting <c>ScopeCursorConflictException</c> is caught here and mapped to
/// <see cref="ApplySnapshotResult.ConcurrentReconcile"/> — the one retryable outcome this endpoint has,
/// since the batch itself was valid and merely lost a race.
///
/// <b>Task 4 adds the <c>Full</c>-mode sweep inside step 10, and it is the one destructive thing on
/// this path that no entry in the request asked for.</b> Between the three diff passes and the first
/// queued delete, a <c>Full</c> batch enumerates its scope's live rows and works out which of them the
/// payload never mentioned — see <see cref="ComputeSweep"/> for the three exclusions that decide it,
/// of which "never sweep a <see cref="ItemInstance.PendingSpawn"/> row" is the one the design names as
/// a correctness mechanism. The empty-payload guard runs in that same gap, before anything is deleted:
/// a sweep that is large while the payload is near-empty is refused whole as
/// <see cref="ApplySnapshotResult.SuspiciousReconcile"/> (422, non-retryable) and recorded as a
/// <see cref="Domain.Snapshots.SuspiciousReconcile"/> document, which is the only write that refused
/// transaction makes. Soft delete plus the retention window is this leaseless design's only undo, and
/// an undo nobody is told to perform is not one.
///
/// <b>The sole-minter rule.</b> The backend is the only minter of an <see cref="ItemInstanceId"/>;
/// this path never inserts. An upsert naming an id the backend never issued is
/// <see cref="SnapshotRejectionReason.UnknownInstance"/> — always, for every parent kind, with no flag
/// that makes it legal. Nothing in Reforger splits or stacks, so the mod never has a legitimate reason
/// to mint, and this is the strongest anti-duplication lever the design has.
/// </summary>
public sealed class ApplySnapshotHandler(
    IItemInstanceRepository repository,
    IWorldSettingsRepository settingsRepository,
    IAppliedBatchRepository appliedBatchRepository,
    IScopeCursorRepository scopeCursorRepository,
    ISuspiciousReconcileRepository suspiciousReconcileRepository,
    IMediator mediator,
    TimeProvider timeProvider)
    : IRequestHandler<ApplySnapshotCommand, ApplySnapshotResult>
{
    /// <summary>The three derived fields an instance inherits from wherever its container chain anchors — see <see cref="ItemInstance.RewriteResolvedRoots"/>.</summary>
    private readonly record struct ResolvedRoots(CharacterId? RootCharacterId, GameServerId? RootGameServerId, DateTimeOffset? ExpiresAt);

    public async ValueTask<ApplySnapshotResult> Handle(ApplySnapshotCommand request, CancellationToken cancellationToken)
    {
        // Step 1: duplicate instanceId, across upserts and deletes combined — see
        // ApplySnapshotResult.DuplicateInstanceId's doc comment. Purely in-memory, checked before any
        // other work. Also load-bearing for everything below: it is what lets one rejection dictionary
        // key on instance id across both arrays without an upsert and a delete colliding.
        var seenIds = new HashSet<ItemInstanceId>();
        foreach (var instanceId in request.Upserts.Select(x => x.InstanceId).Concat(request.Deletes.Select(x => x.InstanceId)))
        {
            if (!seenIds.Add(instanceId))
            {
                return new ApplySnapshotResult.DuplicateInstanceId(instanceId);
            }
        }

        // Step 2: the Full-mode sequence sanity bounds (fix round 1 item 1's ceiling; fix round 2 item 4
        // adds the lower bound) — purely in memory, no Postgres touch, so it costs nothing extra even
        // for a batch that also fails step 1. A non-Full batch, or a Full one whose Sequence hasn't
        // survived this far as non-null (an endpoint-bypassing caller — see RequireSequence below),
        // skips this check entirely; the "sequence is required when mode is Full" rule is the
        // endpoint's own, enforced before this command is ever constructed. The lower bound is a
        // symmetry-and-reasoning-cost argument, not a correctness one: a negative sequence on a virgin
        // scope self-heals the moment a real, non-negative one arrives (same as revision), but a
        // one-line addition to a bounds check that already exists is cheaper than a documented,
        // asymmetric hole.
        if (request.Mode == SnapshotMode.Full
            && request.Sequence is { } candidateSequence
            && (candidateSequence < 0 || candidateSequence > ScopeCursor.MaxSequence))
        {
            return new ApplySnapshotResult.SequenceOutOfRange(candidateSequence, ScopeCursor.MaxSequence);
        }

        // Step 2b (task 4): a Full batch's scope must be one bounded Character or Container. Purely in
        // memory, alongside the sequence bounds and for the same reason — the checks that guard a
        // destructive capability belong before anything is read, not after. A Full reconcile now
        // soft-deletes whatever its scope holds and its payload doesn't mention, so an unbounded scope
        // is not a validation nicety: it is the difference between reconciling one player's inventory
        // and reconciling the deployment's. See ApplySnapshotResult.UnsupportedFullScope for why the
        // missing-companion-id case is refused here rather than left to ScopeCursor.BuildKey's bare
        // InvalidOperationException the way tasks 1-3 left it.
        if (request.Mode == SnapshotMode.Full && !HasBoundedScope(request))
        {
            return new ApplySnapshotResult.UnsupportedFullScope(request.ScopeKind);
        }

        // Step 3: settings load (needed for both the caps below and BatchIdRetentionSeconds) and the
        // batch-level idempotency lookup (task 3). Fix round 2, item 3: the lookup key is now the
        // composite AppliedBatch.BuildKey(gameServerId, batchId), not the raw batchId alone — see
        // AppliedBatch's own class doc comment for why fix round 1's separate GameServerId equality
        // check only closed the read half of the cross-tenant leak, and the composite key is what
        // closes the write half too. A hit returns the originally-stored response verbatim; nothing
        // below this point runs.
        var settings = await settingsRepository.GetAsync(cancellationToken);

        var appliedBatchKey = AppliedBatch.BuildKey(request.GameServerId, request.BatchId);
        var priorBatch = await appliedBatchRepository.FindAsync(appliedBatchKey, cancellationToken);
        if (priorBatch is not null
            && timeProvider.GetUtcNow() - priorBatch.AppliedAt <= TimeSpan.FromSeconds(settings.BatchIdRetentionSeconds))
        {
            return ToReplayResult(priorBatch);
        }

        // Step 4: count caps, before a single ItemInstance row is read or a cross-module call made —
        // the caps double as transaction-duration caps on the write below.
        if (request.Upserts.Count > settings.MaxUpsertsPerBatch)
        {
            return new ApplySnapshotResult.BatchTooLarge("upserts", request.Upserts.Count, settings.MaxUpsertsPerBatch);
        }

        if (request.Deletes.Count > settings.MaxDeletesPerBatch)
        {
            return new ApplySnapshotResult.BatchTooLarge("deletes", request.Deletes.Count, settings.MaxDeletesPerBatch);
        }

        // Per-instance rejections from here on — first reason wins, later steps skip an instance
        // that's already rejected rather than re-evaluating (or overwriting) it. This is also why the
        // order matters beyond cost: a cheaper check's verdict is never overwritten by a pricier one.
        var rejections = new Dictionary<ItemInstanceId, SnapshotRejectionReason>();

        // Step 5: scalar bounds. The cheapest per-instance check there is — a handful of integer and
        // float comparisons, no allocation, no I/O — so it goes first and its verdict wins over every
        // later reason. Deliberately in this in-memory phase rather than in the diff below: an entry
        // whose revision is negative or whose durability isn't a fraction is nonsense on its face,
        // and the "a malformed batch never touches Postgres" property only holds if that is settled
        // before the load round trip.
        RejectOutOfRangeScalars(request, rejections);

        // Step 6: attribute caps. Reuses ItemAttributes.Create's own validation (16 keys / 64-char
        // keys / 256-char values) rather than re-implementing the caps here.
        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId))
            {
                continue;
            }

            try
            {
                ItemAttributes.Create(upsert.Attributes);
            }
            catch (AttributeLimitExceededException)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.AttributeLimit;
            }
        }

        // Step 7: the in-batch parent graph, cycle + max-depth guard — see ContainerBatchGraph's own
        // doc comment. Built from every Container-parented upsert regardless of prior rejection: the
        // graph's topology is real data independent of whether that node's own attributes happened to
        // be invalid, and a descendant's depth still has to walk through it. This pass sees only the
        // edges the batch itself declares; step 10 re-runs the same walk over those edges merged with
        // the stored ones, which is what catches a chain that leaves the batch. Running it twice is
        // deliberate — this half costs nothing and keeps a purely in-batch cycle from ever reaching
        // Postgres.
        var containerParentByInstanceId = request.Upserts
            .Where(x => x.ParentKind == ParentKind.Container && x.ParentContainerInstanceId is not null)
            .ToDictionary(x => x.InstanceId, x => x.ParentContainerInstanceId!.Value);

        RejectContainerCycles(request, containerParentByInstanceId, rejections);

        // Step 8: one batched catalog check for every distinct itemId still in play — dispatched once
        // regardless of batch size, per ItemCatalogEntriesQuery's own "batched by design" contract.
        var candidateItemIds = request.Upserts
            .Where(x => !rejections.ContainsKey(x.InstanceId))
            .Select(x => x.ItemId)
            .Distinct()
            .ToList();

        var catalogEntries = candidateItemIds.Count == 0
            ? new Dictionary<ItemId, ItemCatalogEntry>()
            : await mediator.Send(new ItemCatalogEntriesQuery(candidateItemIds), cancellationToken);

        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId))
            {
                continue;
            }

            if (!catalogEntries.ContainsKey(upsert.ItemId))
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.UnknownItem;
            }
        }

        // Step 9: one batched server guard, covering both the batch's own declared scope character (if
        // any) and every distinct character named directly by a Character-parented upsert still in
        // play — dispatched once regardless of batch size, same as step 8. Guarded on
        // Character.CurrentServerId, never SessionActive, which Character.cs documents as unreliable
        // after an ungraceful gameserver crash. Container/World-parented upserts and every delete have
        // no character to ask about; they guard on the stored row's own denormalised RootGameServerId
        // in step 10, once it has been read.
        var scopeCharacterId = request.ScopeKind == SnapshotScopeKind.Character ? request.ScopeCharacterId : null;

        var candidateCharacterIds = new HashSet<CharacterId>();
        if (scopeCharacterId is { } sc)
        {
            candidateCharacterIds.Add(sc);
        }

        foreach (var upsert in request.Upserts)
        {
            if (!rejections.ContainsKey(upsert.InstanceId)
                && upsert.ParentKind == ParentKind.Character
                && upsert.ParentCharacterId is { } parentCharacterId)
            {
                candidateCharacterIds.Add(parentCharacterId);
            }
        }

        var onThisServer = candidateCharacterIds.Count == 0
            ? new HashSet<CharacterId>()
            : await mediator.Send(new CharactersOnServerQuery(request.GameServerId, candidateCharacterIds.ToList()), cancellationToken);

        // The batch's own scope character not being on this server invalidates the whole batch — see
        // ApplySnapshotResult.WrongServer's doc comment. Checked before any per-instance NotOnThisServer
        // assignment: if the batch's own subject isn't here, reporting every instance individually adds
        // nothing.
        if (scopeCharacterId is { } sc2 && !onThisServer.Contains(sc2))
        {
            return new ApplySnapshotResult.WrongServer();
        }

        // Task 3: the Full-mode sequence gate, Character-scope half. Checked only now that the scope
        // character has just been proven reachable from this gameserver above — never before it, so a
        // stale-sequence rejection can never leak a scope's LastAppliedSequence to a caller who has no
        // business asking about it in the first place. The Container-scope half lives in ApplyAsync
        // instead, because a Container scope's own reachability can't be settled until its row is
        // loaded there — see that check's own comment for why checking both halves in the same place
        // would cost every Character-scoped batch an extra load it doesn't need.
        if (request.Mode == SnapshotMode.Full && scopeCharacterId is { } scopeCharacterForCursor)
        {
            var sequence = RequireSequence(request);
            var scopeKey = ScopeCursor.BuildKey(SnapshotScopeKind.Character, scopeCharacterForCursor, null);
            var cursor = await scopeCursorRepository.FindAsync(scopeKey, cancellationToken);
            if (cursor is not null && sequence <= cursor.LastAppliedSequence)
            {
                return new ApplySnapshotResult.StaleSequence(cursor.LastAppliedSequence);
            }
        }

        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId) || upsert.ParentKind != ParentKind.Character)
            {
                continue;
            }

            if (upsert.ParentCharacterId is not { } parentCharacterId || !onThisServer.Contains(parentCharacterId))
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.NotOnThisServer;
            }
        }

        // Step 10: the one load round trip, the diff, and the write. Wrapped here — the Application
        // layer, never the endpoint — because a lost ScopeCursor race is exactly the "domain guard
        // exception representing a business-rule outcome the caller can reasonably trigger" case
        // ARCHITECTURE.md §9e's catch-and-map convention exists for (fix round 1, item 7): the batch
        // did nothing wrong, it just lost a race, and the caller (the Bridge) needs a clean, retryable
        // union case rather than an unhandled 500.
        try
        {
            return await ApplyAsync(request, settings, catalogEntries, containerParentByInstanceId, rejections, cancellationToken);
        }
        catch (ScopeCursorConflictException)
        {
            return new ApplySnapshotResult.ConcurrentReconcile();
        }
    }

    /// <summary>
    /// Reconstructs the byte-identical <see cref="ApplySnapshotResult.Applied"/> this <c>batchId</c>
    /// produced the first time it was applied, from the stored <see cref="AppliedBatch"/> record — see
    /// that type's own doc comment. The only field that ever differs from the original response is
    /// <see cref="ApplySnapshotResult.Applied.ReplayOfPriorBatch"/>, which is <c>false</c> on the record
    /// (it describes the original application) and always <c>true</c> on what this method returns.
    /// </summary>
    private static ApplySnapshotResult.Applied ToReplayResult(AppliedBatch stored) => new(
        stored.BatchId,
        stored.Sequence,
        stored.AppliedCount,
        stored.SkippedNoOp,
        stored.Deleted,
        stored.CascadeDeleted,
        stored.Swept,
        stored.Rejected.Select(x => new SnapshotRejection(x.InstanceId, x.Reason)).ToList(),
        ReplayOfPriorBatch: true);

    /// <summary>
    /// Whether this batch's declared scope names one bounded set of rows — a single character or a
    /// single container, with the companion id that kind requires actually present. Task 4's step 2b;
    /// see <see cref="ApplySnapshotResult.UnsupportedFullScope"/>.
    ///
    /// The <c>_ =&gt; false</c> arm is the point of writing this as a switch rather than a pair of null
    /// checks: <see cref="SnapshotScopeKind"/> is append-only by convention, so the day a third member
    /// is added the <c>Full</c> sweep must refuse it rather than fall through to whatever the code
    /// below happens to do with a scope it was never taught to bound.
    /// </summary>
    private static bool HasBoundedScope(ApplySnapshotCommand request) => request.ScopeKind switch
    {
        SnapshotScopeKind.Character => request.ScopeCharacterId is not null,
        SnapshotScopeKind.Container => request.ScopeContainerInstanceId is not null,
        _ => false,
    };

    /// <summary>
    /// Fix round 1, item 4. <c>ApplySnapshotCommand.Sequence</c> is only guaranteed non-null by the
    /// <b>endpoint's</b> own "sequence is required when mode is Full" validation — a direct,
    /// non-HTTP caller (every test in this module included) can construct a <c>Full</c> command with a
    /// null <c>Sequence</c> and reach this handler regardless. The gate above short-circuits on a
    /// virgin scope (<c>cursor is null</c>), so that path alone never dereferences <c>Sequence</c> —
    /// but every path that actually needs the value (this gate when a cursor already exists, and the
    /// unconditional advance at the end of <see cref="ApplyAsync"/>) must fail loudly rather than throw
    /// an unlabelled <c>Nullable.Value</c> exception. Mirrors <see cref="ScopeCursor.BuildKey"/>'s own
    /// fallback one call site away: a bare <see cref="InvalidOperationException"/> is the deliberate
    /// signal for a programming error on a path no valid HTTP request can reach, not a caller-triggered
    /// business rule this handler is expected to catch and map.
    /// </summary>
    private static long RequireSequence(ApplySnapshotCommand request)
        => request.Sequence
            ?? throw new InvalidOperationException(
                "ApplySnapshotCommand.Sequence must be set when Mode is Full — the endpoint validates " +
                "this before dispatch, so reaching here with a null Sequence means a non-HTTP caller " +
                "constructed the command directly without that check.");

    /// <summary>
    /// Step 5. Rejects an entry whose typed scalars can't mean anything, before the load round trip —
    /// a negative <c>revision</c> (the LWW key is monotonic and backend-minted rows start at 0), a
    /// <c>durability</c> outside the 0..1 fraction it is defined as (NaN included, since every
    /// comparison against NaN is false and an unguarded one would sail through), or a negative
    /// <c>ammo</c>. Deletes carry a revision too and are bounded the same way.
    ///
    /// No upper bound is placed on <c>revision</c> or <c>ammo</c>: <c>revision</c> is a monotonic
    /// counter with no meaningful ceiling, and a magazine's round count is trusted-and-monitored by
    /// design (see <see cref="ItemInstance.Ammo"/>) — capping it here would be this path inventing a
    /// gameplay rule the catalog doesn't express.
    /// </summary>
    private static void RejectOutOfRangeScalars(ApplySnapshotCommand request, Dictionary<ItemInstanceId, SnapshotRejectionReason> rejections)
    {
        foreach (var upsert in request.Upserts)
        {
            var outOfRange = upsert.Revision < 0
                || upsert.Ammo < 0
                || (upsert.Durability is { } durability && !(durability >= 0f && durability <= 1f));

            if (outOfRange)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.ValueOutOfRange;
            }
        }

        foreach (var delete in request.Deletes)
        {
            if (delete.Revision < 0)
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.ValueOutOfRange;
            }
        }
    }

    /// <summary>
    /// Runs <see cref="ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth"/> for every
    /// still-unrejected Container-parented upsert over whatever parent edges
    /// <paramref name="parentByInstanceId"/> holds. Called twice: once in step 7 with the batch's own
    /// edges alone (zero I/O), and once in step 10 with the stored edges merged in. Both
    /// <see cref="ContainerCycleException"/> and <see cref="ContainerDepthExceededException"/> collapse
    /// to the single wire value <see cref="SnapshotRejectionReason.CycleDetected"/> — the taxonomy has
    /// no separate "too deep" value.
    /// </summary>
    private static void RejectContainerCycles(
        ApplySnapshotCommand request,
        IReadOnlyDictionary<ItemInstanceId, ItemInstanceId> parentByInstanceId,
        Dictionary<ItemInstanceId, SnapshotRejectionReason> rejections)
    {
        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId)
                || upsert.ParentKind != ParentKind.Container
                || upsert.ParentContainerInstanceId is not { } containerInstanceId)
            {
                continue;
            }

            try
            {
                ContainerBatchGraph.ValidateNoCycleOrExcessiveDepth(upsert.InstanceId, containerInstanceId, parentByInstanceId);
            }
            catch (ContainerCycleException)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.CycleDetected;
            }
            catch (ContainerDepthExceededException)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.CycleDetected;
            }
        }
    }

    /// <summary>
    /// Step 10. The whole diff, against one primary <c>LoadManyAsync</c> plus a bounded chain walk,
    /// committed in one <c>SaveChangesAsync</c>.
    ///
    /// <b>The primary load</b> takes every id this batch still touches after validation — every
    /// unrejected upsert, every unrejected delete, every container such an upsert parents into, and a
    /// <c>Container</c> scope's own container. Rejected entries are excluded on purpose: a batch whose
    /// every entry is already rejected must not read a single instance row, which is the same
    /// "malformed input never reaches storage" property steps 5-7 establish in memory.
    ///
    /// <b>The chain walks</b> are the two bounded follow-ups the primary load can't cover, and neither
    /// is a load per instance — the thing the design actually forbids. Both are capped at
    /// <see cref="ItemInstance.MaxContainerDepth"/> batched queries per request, constant in batch
    /// size:
    /// <list type="bullet">
    /// <item>upward, before the diff, so the merged cycle/depth walk sees a chain that leaves both the
    /// batch and the primary load's own set rather than stopping at its edge;</item>
    /// <item>downward, after the diff, because moving or deleting a container is meaningless without
    /// reaching what is inside it — see <see cref="WriteResolvedRoots"/> and the cascade below.</item>
    /// </list>
    /// </summary>
    private async ValueTask<ApplySnapshotResult> ApplyAsync(
        ApplySnapshotCommand request,
        WorldSettings settings,
        IReadOnlyDictionary<ItemId, ItemCatalogEntry> catalogEntries,
        IReadOnlyDictionary<ItemInstanceId, ItemInstanceId> containerParentByInstanceId,
        Dictionary<ItemInstanceId, SnapshotRejectionReason> rejections,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Only ids still in play. An entry already rejected in steps 5-9 has nothing left to compare
        // against storage, so reading its row would be pure cost — and, for a batch that is entirely
        // rejected, would turn "a malformed batch never touches Postgres" into "almost never".
        var idsToLoad = new HashSet<ItemInstanceId>();
        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId))
            {
                continue;
            }

            idsToLoad.Add(upsert.InstanceId);
            if (upsert.ParentKind == ParentKind.Container && upsert.ParentContainerInstanceId is { } parentContainerId)
            {
                idsToLoad.Add(parentContainerId);
            }
        }

        foreach (var delete in request.Deletes)
        {
            if (!rejections.ContainsKey(delete.InstanceId))
            {
                idsToLoad.Add(delete.InstanceId);
            }
        }

        // The scope container is loaded unconditionally: it is a batch-level guard, not a per-instance
        // one, so it has to be answered even for a batch whose every entry was already rejected.
        if (request.ScopeKind == SnapshotScopeKind.Container && request.ScopeContainerInstanceId is { } scopeContainerId)
        {
            idsToLoad.Add(scopeContainerId);
        }

        // Short-circuited the same way steps 8 and 9 short-circuit their own batched calls: with every
        // entry rejected there is nothing left to ask about, and the point is that the handler does not
        // ask — not that the repository is clever enough to skip an empty query.
        var loaded = idsToLoad.Count == 0
            ? []
            : await repository.LoadManyAsync(idsToLoad.ToList(), cancellationToken);

        var instances = loaded.ToDictionary(x => x.Id);

        await LoadAncestorClosureAsync(instances, cancellationToken);

        // The Container half of the batch-level scope guard, the counterpart to step 9's Character
        // half — see ApplySnapshotResult.WrongServer. A container the backend never issued, a
        // tombstoned one, a still-pending one, or one whose denormalised RootGameServerId names a
        // different gameserver all mean the same thing here: this batch's declared subject is not
        // reachable from the caller.
        //
        // PendingSpawn is in that list because RootGameServerId is null until a row is actually
        // delivered somewhere, which makes IsOnAnotherServer structurally incapable of rejecting a
        // pending row — and a Container scope carries no character to fall back on the way a Character
        // scope does. A container nobody has taken delivery of yet is not somewhere a gameserver can
        // be standing, so no server may claim to be describing its contents.
        if (request.ScopeKind == SnapshotScopeKind.Container)
        {
            if (request.ScopeContainerInstanceId is not { } scopeId
                || !instances.TryGetValue(scopeId, out var scopeContainer)
                || scopeContainer.RemovedByStaff
                || scopeContainer.PendingSpawn
                || IsOnAnotherServer(scopeContainer, request.GameServerId))
            {
                return new ApplySnapshotResult.WrongServer();
            }
        }

        // Task 3: the Full-mode sequence gate, Container-scope half — the counterpart to the
        // Character-scope half in step 9. It has to live here rather than there because a Container
        // scope's own reachability can't be settled until its row is loaded above; checking it only
        // now, after that proof, is what keeps a stale-sequence rejection from ever leaking a scope's
        // LastAppliedSequence to a caller who was never shown to be allowed to ask about that scope in
        // the first place — same reasoning as the Character-scope half. Guarded on ScopeKind so a
        // Character-scoped batch (already gated in step 9) never re-reads the same cursor twice.
        if (request.Mode == SnapshotMode.Full && request.ScopeKind == SnapshotScopeKind.Container)
        {
            var sequence = RequireSequence(request);
            var scopeKey = ScopeCursor.BuildKey(request.ScopeKind, request.ScopeCharacterId, request.ScopeContainerInstanceId);
            var cursor = await scopeCursorRepository.FindAsync(scopeKey, cancellationToken);
            if (cursor is not null && sequence <= cursor.LastAppliedSequence)
            {
                return new ApplySnapshotResult.StaleSequence(cursor.LastAppliedSequence);
            }
        }

        // The other half of the cycle/depth guard, and the reason it lives here rather than in step 7:
        // the batch's own edges only describe chains that stay inside the batch. Merging the stored
        // rows' own container edges in — batch edges winning, since those are what this batch is about
        // to make true — is what catches a chain that exits the batch into stored state, and the
        // upward closure above is what keeps it from stopping at the loaded set's edge instead.
        //
        // Only *unrejected* batch edges are merged, unlike step 7's pass. Step 7 is asking "is this
        // batch internally consistent", where a rejected node's declared parentage is still real data
        // a sibling's depth has to walk through. This pass is asking "will the post-batch stored graph
        // be acyclic", and a rejected upsert will never be written — asserting its edge here makes the
        // walk reject a perfectly valid sibling for a cycle that neither stored nor post-batch state
        // contains.
        var mergedParents = new Dictionary<ItemInstanceId, ItemInstanceId>();
        foreach (var stored in instances.Values)
        {
            if (stored.ParentKind == ParentKind.Container && stored.ContainerInstanceId is { } storedParentId)
            {
                mergedParents[stored.Id] = storedParentId;
            }
        }

        foreach (var (child, parent) in containerParentByInstanceId)
        {
            if (!rejections.ContainsKey(child))
            {
                mergedParents[child] = parent;
            }
        }

        RejectContainerCycles(request, mergedParents, rejections);

        // The diff runs as three passes rather than one, and the split is a correctness requirement
        // rather than tidiness.
        //
        // The container-parent guard has to answer "will this container still be pending once this
        // batch is done", which is a question about another entry's *outcome*. A single pass can only
        // ever answer it with an intention — either "what has applied so far", which makes the verdict
        // depend on where the two entries happened to sit in an array the wire contract gives no
        // ordering to, or "what the batch set out to upsert", which is what the previous cut used and
        // which unlocks the guard on the strength of an entry that the diff itself then rejects. Both
        // are exploitable the same way: pair a doomed upsert of somebody else's undelivered crate with
        // a real upsert nesting your own item into it, and the second one inherits the victim's
        // RootCharacterId and lands in their inventory.
        //
        // So: pass A settles everything that depends only on a row and its own stored state, pass B
        // settles the container-parent guards against pass A's outcome, and pass C applies what
        // survives. Nothing mutates before pass C, so every guard reads stored state, and the
        // applicable set is a fixed point — the same answer whatever order the entries arrive in.
        var applicable = new HashSet<ItemInstanceId>();

        var appliedInstanceIds = new HashSet<ItemInstanceId>();
        var deletedInstanceIds = new HashSet<ItemInstanceId>();
        var skippedNoOp = 0;

        // ---- Pass A: the row-local checks ----
        foreach (var upsert in request.Upserts)
        {
            if (rejections.ContainsKey(upsert.InstanceId))
            {
                continue;
            }

            // The sole-minter rule (global constraint 3), and the single most important line on this
            // path: an id the backend never issued is rejected, never inserted. There is no parent
            // kind and no flag that makes it legal.
            if (!instances.TryGetValue(upsert.InstanceId, out var stored))
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.UnknownInstance;
                continue;
            }

            // The sticky tombstone: a staff-removed row is never resurrected by an upsert, whatever
            // revision it claims.
            if (stored.RemovedByStaff)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.RemovedByStaff;
                continue;
            }

            // The non-Character half of the server guard. A Character-parented upsert was already
            // checked in step 9 against the live Character.CurrentServerId, which is the stronger
            // authority; a Container- or World-parented one names no character at all, so the stored
            // row's own denormalised RootGameServerId is what says where this thing currently is.
            if (upsert.ParentKind != ParentKind.Character && IsOnAnotherServer(stored, request.GameServerId))
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.NotOnThisServer;
                continue;
            }

            // ...and the guard that check structurally cannot make. A PendingSpawn row carries a null
            // RootGameServerId by construction (ItemInstance.Register: minted pending, undelivered,
            // rootless), so IsOnAnotherServer above returns false for it no matter which gameserver is
            // calling. Without this, any server holding the id could world-parent another server's
            // paid, undelivered grant onto its own map's ground — where the descendant resolution
            // below would strip RootCharacterId and start a despawn timer, and the character who was
            // owed the item would simply never receive it. A paid item crossing a tenancy boundary is
            // exactly what the server guard exists to prevent.
            //
            // The authority that *can* answer is the character the row is owed to. Requiring the batch
            // to be Character-scoped on that character reuses step 9's already-proven
            // Character.CurrentServerId check at zero extra I/O — the same authority
            // AcknowledgeSpawnsHandler uses to decide whether an ack may clear this very flag. It also
            // costs the honest path nothing: a mod adopting a grant is describing that character's
            // inventory, which is a Character-scoped batch on that character by construction.
            if (stored.PendingSpawn && !IsAdoptableByScope(stored, request))
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.NotOnThisServer;
                continue;
            }

            // A known id carrying a different itemId is an alert, never an item swap — a UUIDv4
            // collision, a mod bug, or something worse.
            if (stored.ItemId != upsert.ItemId)
            {
                rejections[upsert.InstanceId] = SnapshotRejectionReason.IdentityConflict;
                continue;
            }

            applicable.Add(upsert.InstanceId);
        }

        // ---- Pass B: the container-parent guards, iterated to a fixed point ----
        //
        // Iterated rather than single-shot because a container can itself be rejected here: a pending
        // crate whose own entry nests it into some third container that this pass throws out never
        // stops being pending, so anything the first round let in on its account has to be
        // reconsidered. Each round can only remove entries, so this shrinks monotonically and
        // terminates; in practice it settles in one round, since a chain of pending containers moving
        // in one batch is not a shape a real snapshot produces.
        bool changed;
        do
        {
            changed = false;

            foreach (var upsert in request.Upserts)
            {
                if (!applicable.Contains(upsert.InstanceId) || upsert.ParentKind != ParentKind.Container)
                {
                    continue;
                }

                SnapshotRejectionReason? reason;

                // The sole-minter rule applied one level up: an instance may only be nested inside a
                // container the backend also issued. A missing parent here is either an id the mod
                // invented or one this same batch already rejected as UnknownInstance, and in both
                // cases there is no chain to resolve roots through.
                if (upsert.ParentContainerInstanceId is not { } containerId
                    || !instances.TryGetValue(containerId, out var container))
                {
                    reason = SnapshotRejectionReason.UnknownInstance;
                }

                // And both server guards again, one level up. A crate sitting on a different map is
                // the obvious tenancy violation; a crate nobody has taken delivery of yet is the same
                // hole the row-level check has, for the same reason — RootGameServerId is null until
                // delivery, so IsOnAnotherServer cannot reject it. This is the container-parent
                // counterpart of the scope-container rule above, which the two must agree on.
                //
                // Unless this batch is genuinely adopting that container too, which is the honest case
                // of a mod spawning a granted crate and reporting its contents in one snapshot. That
                // asks about the container entry's outcome, not its existence — an entry the diff
                // rejects leaves the crate pending, and letting it unlock this guard is what made the
                // whole check bypassable.
                else if (container.PendingSpawn && !applicable.Contains(container.Id))
                {
                    reason = SnapshotRejectionReason.NotOnThisServer;
                }
                else if (IsOnAnotherServer(container, request.GameServerId))
                {
                    reason = SnapshotRejectionReason.NotOnThisServer;
                }
                else
                {
                    continue;
                }

                rejections[upsert.InstanceId] = reason.Value;
                applicable.Remove(upsert.InstanceId);
                changed = true;
            }
        }
        while (changed);

        // ---- Pass C: revision last-write-wins, then apply ----
        foreach (var upsert in request.Upserts)
        {
            if (!applicable.Contains(upsert.InstanceId))
            {
                continue;
            }

            var stored = instances[upsert.InstanceId];

            // A PendingSpawn row's first upsert always applies, whatever revision it carries. Skipping
            // it would be a real bug rather than a nicety: backend-minted rows start at Revision 0, so
            // if the mod's own counter also starts at 0 the item's first real change would be
            // discarded by the LWW comparison below and the row would never leave the pending queue.
            // The upsert reaching us at all is proof the mod adopted the instance, which is what makes
            // this the implicit-ack path that survives a lost ack — and pass A's scope guard is what
            // makes "the mod" mean the one server that could legitimately have spawned it.
            //
            // That is also why pass B can treat "in the applicable set" as "will stop being pending":
            // a pending row never reaches the comparison below, so for exactly the rows that guard
            // cares about, surviving pass B and applying are the same thing.
            if (!stored.PendingSpawn)
            {
                if (upsert.Revision < stored.Revision)
                {
                    // Integer comparison, never a JSON comparison of the two documents.
                    skippedNoOp++;
                    continue;
                }

                if (upsert.Revision == stored.Revision)
                {
                    if (ContentMatches(stored, upsert))
                    {
                        skippedNoOp++;
                        continue;
                    }

                    // Same revision, different content: two writers disagree about what this instance
                    // is at the same point in its history. Never a silent overwrite.
                    rejections[upsert.InstanceId] = SnapshotRejectionReason.IdentityConflict;
                    continue;
                }
            }

            stored.ApplySnapshot(
                upsert.Revision,
                upsert.ParentKind,
                upsert.ParentCharacterId,
                upsert.ParentContainerInstanceId,
                upsert.Slot,
                upsert.Transform,
                upsert.Durability,
                upsert.Ammo,
                ItemAttributes.Create(upsert.Attributes),
                now);

            appliedInstanceIds.Add(upsert.InstanceId);
        }

        foreach (var delete in request.Deletes)
        {
            if (rejections.ContainsKey(delete.InstanceId))
            {
                continue;
            }

            if (!instances.TryGetValue(delete.InstanceId, out var stored))
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.UnknownInstance;
                continue;
            }

            if (stored.RemovedByStaff)
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.RemovedByStaff;
                continue;
            }

            // A delete names no parent, so the stored row's denormalised RootGameServerId is the only
            // thing that can say whether this instance is even on the calling gameserver...
            if (IsOnAnotherServer(stored, request.GameServerId))
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.NotOnThisServer;
                continue;
            }

            // ...and for a pending row that field is null, so the same scope guard the upsert path
            // uses applies here too. Destroying another character's paid, undelivered grant from a
            // foreign server is the same tenancy violation as adopting it.
            if (stored.PendingSpawn && !IsAdoptableByScope(stored, request))
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.NotOnThisServer;
                continue;
            }

            // Asymmetric with the upsert path above, deliberately. A stale upsert is a harmless no-op
            // — the backend already holds strictly newer content for that row — so it is skipped and
            // counted. A stale delete is destructive: an out-of-order replay of an older buffered
            // delete would erase state the backend already knows is newer, so it is reported rather
            // than silently swallowed. Equal revisions still delete, which is what a PendingSpawn row
            // consumed before its ack ever landed looks like (both sides at 0).
            if (delete.Revision < stored.Revision)
            {
                rejections[delete.InstanceId] = SnapshotRejectionReason.StaleRevision;
                continue;
            }

            deletedInstanceIds.Add(delete.InstanceId);
        }

        // ---- Task 4: the Full-mode sweep, computed and guarded before a single row is deleted ----
        //
        // A Full batch means "this is everything in this scope", so whatever the scope holds and the
        // payload never mentions is gone — see ComputeSweep for exactly which rows that is and, more
        // importantly, which it deliberately is not.
        //
        // Position matters twice over. It is *after* the three diff passes because the sweep's own
        // protection rules have to read post-diff parentage: a row this batch just moved into a
        // container must protect that container, and a container it just moved a row out of must not be
        // protected by the stale edge. It is *before* CascadeDeletes below because that is the first
        // line in this handler that queues a delete — so the empty-payload guard genuinely runs first,
        // with storage still untouched. Nothing above this point has queued a write of any kind (the
        // three passes only mutate in-memory copies, and this module's session is a Marten
        // LightweightSession with no dirty tracking), which is what lets the refusal path below return
        // after a SaveChangesAsync that commits the staff record and nothing else — still exactly one
        // SaveChangesAsync per batch, global constraint 6 intact.
        var sweepable = new Dictionary<ItemInstanceId, ItemInstance>();
        if (request.Mode == SnapshotMode.Full)
        {
            var scopeRows = await LoadScopeRowsAsync(request, instances, cancellationToken);
            sweepable = ComputeSweep(request, instances, scopeRows, deletedInstanceIds, rejections);

            // The empty-payload guard. The scale test is required and is never sufficient on its own: a
            // large sweep by itself is exactly what an honest mass-loss reconcile looks like, and the
            // guard must not stand in its way. What makes a sweep suspicious is scale paired with a
            // payload that offers too little evidence for it — which a server that booted with a failed
            // mod load, or one caught mid-split, produces while cheerfully reporting a world it cannot
            // actually see. Soft delete plus the retention window is the only undo this leaseless design
            // has, so the refusal is what makes a bad reconcile recoverable rather than terminal.
            //
            // "Too little evidence" is two independent tests, because either alone leaves a hole the
            // other closes:
            //
            //   * NEAR-EMPTY: fewer than a handful of upserts. Catches the classic "the mod sees
            //     nothing" report. On its own it is trivially disarmed — a mod that names three items
            //     could wipe an inventory of any size at all (review round 1).
            //   * DISPROPORTIONATE: the sweep accounts for essentially the whole scope. Catches the same
            //     failure wearing a slightly larger payload, and it is the test that actually scales:
            //     what matters is not how many rows the mod reported but how small a share of what
            //     should have been there that was.
            //
            // <b>The gate measures rows that were AT STAKE — never the size of the sweep, and never the
            // raw size of the scope either.</b> Both halves of that took a review round to land.
            //
            // Round 2: it must not be `sweepable`. Rules 3-5 protect rows from the sweep, so they shrink
            // that number — and gating on it meant every row they saved also made the guard quieter
            // about the ones they didn't. A 46-row character whose mod reported NOTHING AT ALL ended up
            // sweeping only the five rows not sitting inside a protected crate, slipped under the
            // threshold, and lost those five with no staff record: the guard falling silent in precisely
            // the scenario it exists for. The blast radius shrinking is not a reason to stop noticing.
            //
            // Round 3: nor may it be the raw scope size, because LoadScopeRowsAsync deliberately does not
            // filter PendingSpawn or RemovedByStaff (the anchor walk needs those rows as parents) while
            // ComputeSweep's rules 1 and 2 refuse to sweep either. Counting them meant counting rows the
            // sweep can never touch, and it over-fired on correct batches: a character with 30 undelivered
            // grants and 2 carried items sending a perfectly accurate Full naming both carried rows was
            // refused with WouldHaveSwept == 0, its two legitimate upserts discarded. Worse, that is not
            // self-correcting the way a threshold trip normally is — the condition is a property of
            // stored state, so every subsequent reconcile is refused identically and writes another staff
            // record under a fresh batchId. It also broke "logged out naked" permanently for any
            // character holding 26+ staff tombstones, since a tombstone is a live row kept forever.
            //
            // So: live, non-pending, non-tombstoned. That is what "how much was at stake" actually means,
            // it keeps protection and belief independent (protection decides what is deleted, the guard
            // decides whether a claim this large deserves belief), and it is the same set rules 1 and 2
            // leave eligible — so the guard and the sweep now agree about what they are talking about.
            //
            // The proportional arm divides by the same number for the same reason: rows that can never
            // be swept would only dilute the ratio and blunt the arm.
            //
            // And the gate is still what keeps "the player logged out naked" working, which is why it
            // cannot simply be dropped: a character who genuinely holds a handful of things and now holds
            // nothing is 100% disproportionate by construction, and must still reconcile — their scope
            // never reaches the threshold in the first place. See WorldSettings for all three numbers and
            // why they are tunable rather than constants.
            var eligibleScopeRowCount = scopeRows.Values.Count(x => !x.PendingSpawn && !x.RemovedByStaff);

            var nearEmptyPayload = request.Upserts.Count < settings.SuspiciousReconcileUpsertsThreshold;
            var disproportionateSweep = sweepable.Count * 100L
                >= (long)eligibleScopeRowCount * settings.SuspiciousReconcileSweptPercentThreshold;

            if (eligibleScopeRowCount > settings.SuspiciousReconcileScopeRowsThreshold
                && (nearEmptyPayload || disproportionateSweep))
            {
                suspiciousReconcileRepository.Store(new Domain.Snapshots.SuspiciousReconcile
                {
                    Id = Domain.Snapshots.SuspiciousReconcile.BuildKey(request.GameServerId, request.BatchId),
                    BatchId = request.BatchId,
                    GameServerId = request.GameServerId,
                    ScopeKind = request.ScopeKind,
                    // Only the companion id the declared kind actually uses — the command type carries
                    // both fields, and a staff record that echoed a stray one back would name an anchor
                    // this batch was never scoped to. Same ScopeKind gating as everywhere else on this
                    // path (review round 2).
                    ScopeCharacterId = request.ScopeKind == SnapshotScopeKind.Character ? request.ScopeCharacterId : null,
                    ScopeContainerInstanceId = request.ScopeKind == SnapshotScopeKind.Container ? request.ScopeContainerInstanceId : null,
                    Sequence = request.Sequence,
                    WouldHaveSwept = sweepable.Count,
                    ScopeRowCount = eligibleScopeRowCount,
                    UpsertCount = request.Upserts.Count,
                    DeleteCount = request.Deletes.Count,
                    ScopeRowsThreshold = settings.SuspiciousReconcileScopeRowsThreshold,
                    UpsertsThreshold = settings.SuspiciousReconcileUpsertsThreshold,
                    SweptPercentThreshold = settings.SuspiciousReconcileSweptPercentThreshold,
                    RecordedAt = now,
                });

                // No AppliedBatch record (this batch is not Applied, so there is no response body to
                // replay) and no ScopeCursor advance (the reconcile did not happen, so the corrected
                // one must still be accepted at this same sequence). The one thing this transaction
                // writes is the staff record.
                await repository.SaveChangesAsync(cancellationToken);

                return new ApplySnapshotResult.SuspiciousReconcile(
                    sweepable.Count,
                    eligibleScopeRowCount,
                    request.Upserts.Count,
                    settings.SuspiciousReconcileScopeRowsThreshold,
                    settings.SuspiciousReconcileUpsertsThreshold,
                    settings.SuspiciousReconcileSweptPercentThreshold);
            }
        }

        // Everything the batch changed has to reach what is nested inside it, so pull the subtrees of
        // every applied and every deleted row in before writing anything. Bounded by container depth,
        // batched per level.
        var subtreeSeeds = new HashSet<ItemInstanceId>(appliedInstanceIds);
        subtreeSeeds.UnionWith(deletedInstanceIds);
        await LoadDescendantClosureAsync(instances, subtreeSeeds, cancellationToken);

        var removedInstanceIds = CascadeDeletes(instances, deletedInstanceIds);
        var cascadeDeletedCount = removedInstanceIds.Count - deletedInstanceIds.Count;

        // ...and only now, past the guard, does the sweep actually delete. Deliberately *not* run
        // through CascadeDeletes: a swept row's descendants are already sweep candidates in their own
        // right (a Character scope is keyed on the denormalised RootCharacterId every descendant
        // inherits; a Container scope walks the whole subtree), so the cascade would add nothing but
        // reach — and the one thing it would reach that the sweep itself will not touch is exactly what
        // must never be touched here: a PendingSpawn row, and any row a surviving row is nested inside.
        // ComputeSweep's protection rules would be undone one hop later by a cascade that ignored them.
        var sweptCount = 0;
        foreach (var swept in sweepable.Values)
        {
            // Skip anything an explicit delete's cascade already removed above — soft-deleting it twice
            // would be harmless but would double-count it against a `swept` number the caller reads as
            // "rows that went away because I didn't mention them".
            if (!removedInstanceIds.Add(swept.Id))
            {
                continue;
            }

            repository.SoftDelete(swept);
            sweptCount++;
        }

        // A row the same batch both upserted and cascaded out of existence is not "applied" — nothing
        // of what the upsert said survives, and counting it would make AppliedCount describe writes
        // that never happened. Dropping it here also keeps Store() and Delete() off the same id in one
        // SaveChangesAsync, which ARCHITECTURE.md §9e gotcha 10 makes a silent data-loss bug rather
        // than an error.
        appliedInstanceIds.ExceptWith(removedInstanceIds);

        WriteResolvedRoots(request, settings, catalogEntries, instances, appliedInstanceIds, removedInstanceIds, now);

        var result = new ApplySnapshotResult.Applied(
            request.BatchId,
            request.Sequence,
            AppliedCount: appliedInstanceIds.Count,
            SkippedNoOp: skippedNoOp,
            // Counts only the deletes this batch was asked for, so the caller's own arithmetic still
            // closes: deleted + (rejected entries from the deletes array) == deletes.length.
            Deleted: deletedInstanceIds.Count,
            // ...and everything that went with them, reported separately rather than hidden. See
            // CascadeDeletes. Captured before the sweep folded its own ids into removedInstanceIds, so
            // this stays "descendants of a requested delete" and never quietly absorbs swept rows.
            CascadeDeleted: cascadeDeletedCount,
            // Task 4: rows the scope held that a Full payload never mentioned. A third number rather
            // than a bigger version of either of the two above — see Applied's own doc comment.
            Swept: sweptCount,
            Rejected: rejections.Select(x => new SnapshotRejection(x.Key, x.Value)).ToList(),
            ReplayOfPriorBatch: false);

        // Task 3: queue the idempotency record — and, for a Full batch, advance the scope's cursor —
        // before the one SaveChangesAsync below, so both join the exact same Postgres transaction as
        // every ItemInstance write above (global constraint 6: one SaveChangesAsync per batch). A crash
        // between here and that call leaves neither queued write committed, which is the same
        // all-or-nothing property the rest of this handler already relies on: there is no window where
        // storage was mutated but this batchId reads back as "never applied", or where the cursor
        // advanced without the batch it describes actually landing.
        appliedBatchRepository.Store(new AppliedBatch
        {
            Id = AppliedBatch.BuildKey(request.GameServerId, request.BatchId),
            BatchId = result.BatchId,
            GameServerId = request.GameServerId,
            AppliedAt = now,
            Sequence = result.Sequence,
            AppliedCount = result.AppliedCount,
            SkippedNoOp = result.SkippedNoOp,
            Deleted = result.Deleted,
            CascadeDeleted = result.CascadeDeleted,
            Swept = result.Swept,
            Rejected = result.Rejected.Select(x => new AppliedBatchRejection(x.InstanceId, x.Reason)).ToList(),
        });

        if (request.Mode == SnapshotMode.Full)
        {
            var scopeKey = ScopeCursor.BuildKey(request.ScopeKind, request.ScopeCharacterId, request.ScopeContainerInstanceId);
            await scopeCursorRepository.AdvanceAsync(scopeKey, RequireSequence(request), now, cancellationToken);
        }

        // One SaveChangesAsync for the whole batch: one Postgres transaction, all-or-nothing.
        await repository.SaveChangesAsync(cancellationToken);

        return result;
    }

    /// <summary>
    /// Task 4: every live row the batch's declared scope currently holds — the set a
    /// <see cref="SnapshotMode.Full"/> payload is claiming to be a complete description of.
    ///
    /// A <c>Character</c> scope reads the denormalised <see cref="ItemInstance.RootCharacterId"/>,
    /// which is exactly the right key: it is what the hot carried-inventory read already uses, and it
    /// resolves transitively, so a rifle's magazine three containers deep is in the scope without the
    /// sweep having to know the tree's shape. A <c>Container</c> scope has no such denormalisation to
    /// lean on (a crate's contents are only reachable by their parent edges), so it walks down instead
    /// — the same bounded, batched-per-level walk as <see cref="LoadDescendantClosureAsync"/>, at most
    /// <see cref="ItemInstance.MaxContainerDepth"/> queries, never one per row.
    ///
    /// Neither read filters <see cref="ItemInstance.PendingSpawn"/> or
    /// <see cref="ItemInstance.RemovedByStaff"/>, deliberately, even though
    /// <see cref="ComputeSweep"/> refuses to sweep either. Those rows are still part of "what the scope
    /// holds": they are what a surviving row may be nested inside, and dropping them here would make
    /// the ancestor-protection pass blind to exactly the parents it most needs to see.
    ///
    /// Rows this batch also loaded are overlaid with the loaded copy, because the three diff passes
    /// have already mutated those in memory and the sweep's protection rules read post-diff parentage.
    /// Two reads of one row would otherwise disagree about where it now sits, and the stale one would
    /// win by arriving second.
    /// </summary>
    private async ValueTask<Dictionary<ItemInstanceId, ItemInstance>> LoadScopeRowsAsync(
        ApplySnapshotCommand request,
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> instances,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<ItemInstanceId, ItemInstance>();

        switch (request.ScopeKind)
        {
            case SnapshotScopeKind.Character when request.ScopeCharacterId is { } scopeCharacterId:
                foreach (var row in await repository.FindByRootCharacterAsync(scopeCharacterId, cancellationToken))
                {
                    rows[row.Id] = row;
                }

                break;

            case SnapshotScopeKind.Container when request.ScopeContainerInstanceId is { } scopeContainerId:
                var frontier = new List<ItemInstanceId> { scopeContainerId };
                for (var hop = 0; hop < ItemInstance.MaxContainerDepth && frontier.Count > 0; hop++)
                {
                    var children = await repository.FindChildrenOfManyAsync(frontier, cancellationToken);
                    frontier = [];

                    foreach (var child in children)
                    {
                        // The scope container is what the batch is describing, never part of what it
                        // describes — a crate can never report itself out of existence.
                        if (child.Id != scopeContainerId && rows.TryAdd(child.Id, child))
                        {
                            frontier.Add(child.Id);
                        }
                    }
                }

                break;

            default:
                // Unreachable by construction: step 2b's HasBoundedScope refuses every Full batch that
                // doesn't match one of the two arms above, before this method can be called. A bare
                // InvalidOperationException is the deliberate signal for that, matching
                // ScopeCursor.BuildKey's own fallback one call site away (ARCHITECTURE.md §9e: the
                // catch-and-map convention is for invariants a caller can actually reach).
                throw new InvalidOperationException(
                    $"Cannot enumerate the rows of a {request.ScopeKind} scope with no matching id — "
                    + "a Full batch must have been refused as UnsupportedFullScope before reaching here.");
        }

        foreach (var id in rows.Keys.ToList())
        {
            if (instances.TryGetValue(id, out var alreadyLoaded))
            {
                rows[id] = alreadyLoaded;
            }
        }

        return rows;
    }

    /// <summary>
    /// Task 4: which of the scope's live rows a <see cref="SnapshotMode.Full"/> batch's silence
    /// actually condemns. Everything the payload didn't mention, minus five exclusions that are each
    /// load-bearing rather than defensive tidying.
    ///
    /// <b>1. <see cref="ItemInstance.PendingSpawn"/> rows are never swept</b> — the design's own named
    /// correctness mechanism, and the single most important line here. A pending row's entity does not
    /// exist in the game yet: the backend minted it, nobody has spawned it, and the mod has never seen
    /// it. Its absence from a snapshot therefore carries no information at all, so reading that absence
    /// as "it's gone" destroys a paid, undelivered item on the strength of evidence that was never
    /// offered. (The complementary rule already lives in <c>ItemInstance.ClearPendingOnExplicitDelete</c>:
    /// the flag protects a row from <i>reconcile</i>, never from the mod explicitly saying "this is
    /// gone".)
    ///
    /// <b>2. <see cref="ItemInstance.RemovedByStaff"/> rows are never swept</b> — the tombstone has to
    /// stay findable to stay sticky. It is a live row on purpose, so a later upsert of that id finds it
    /// and is rejected <see cref="SnapshotRejectionReason.RemovedByStaff"/> rather than resurrecting
    /// anything; soft-deleting it would make every read return nothing, and the very next upsert would
    /// come back <see cref="SnapshotRejectionReason.UnknownInstance"/> instead — the sticky tombstone
    /// quietly undone by a sweep that meant no harm.
    ///
    /// <b>3. A row whose <i>post-diff</i> chain no longer anchors in the scope is never swept</b>
    /// (<see cref="AnchorsInScope"/>). Scope membership comes from a stored, denormalised
    /// <see cref="ItemInstance.RootCharacterId"/> (or a stored parent walk), which describes where a row
    /// was <i>before</i> this batch — and the machinery that re-anchors a moved subtree
    /// (<see cref="LoadDescendantClosureAsync"/>, <see cref="WriteResolvedRoots"/>) does not run until
    /// after this. Without this rule, a batch that says "the backpack is now on the ground" or "the
    /// crate now belongs to character B" sweeps every unmentioned row still <i>stored</i> as rooted at
    /// the old scope — which is the entire contents of the thing that just moved. Review round 1
    /// reproduced both against live Postgres: five items in a dropped backpack and three in a handed-over
    /// crate, all silently deleted, and the guard blind to both (five rows is nowhere near the row
    /// threshold, and either payload's upsert count disarmed the near-empty arm outright).
    ///
    /// <b>4. A row whose container the payload never mentions <i>at all</i> is never swept.</b> This is
    /// the downward counterpart of rule 5, and rule 3 cannot cover it — a crate that stays exactly where
    /// it was still anchors in scope, so its contents remain candidates on anchoring alone. The rule is
    /// a claim-of-knowledge rule: if the mod speaks about a container, it is claiming to know what is in
    /// it, so contents it then omits are genuinely gone and stay sweepable; if it never mentions the
    /// container, it is claiming nothing whatsoever about the inside, and silence about a thing you
    /// never looked in is not evidence of absence. That is the same rule
    /// <see cref="LoadDescendantClosureAsync"/> already documents from the other side — "the mod is
    /// under no obligation to re-report the inside of a crate it merely moved" — and it is the contract
    /// the Bridge is held to: a <c>Full</c> must enumerate the contents of any container it mentions;
    /// contents of a container it does not mention are left alone.
    ///
    /// "Mentions" is deliberately narrow: the container's own id appears in <c>upserts</c> or
    /// <c>deletes</c>, or it is the batch's own scope container (whose contents are the batch's entire
    /// subject). A bare reference as some other row's <c>parent</c> does <b>not</b> count, because
    /// widening this set can only ever delete <i>more</i>, and the shape it would widen for — the mod
    /// reporting a magazine but forgetting the rifle — is already covered non-destructively by rule 5.
    ///
    /// <b>5. A container any surviving row is nested inside is never swept</b>, iterated to a fixed
    /// point up the chain. Without it, a mod that reports the magazine but forgets the rifle it sits in
    /// deletes the rifle and leaves the magazine parented to a row that no longer exists — still
    /// answering the carried-inventory read, since <see cref="ItemInstance.RootCharacterId"/> resolves
    /// through the chain. This is the reason the sweep does not need (and must not use) the cascade
    /// that an explicit delete uses: rather than reaching further, it stops short, and stopping short
    /// is what keeps a payload's own omissions from compounding.
    ///
    /// An explicitly <i>deleted</i> row is excluded from that survivor scan (review round 1): it is not
    /// a survivor, and the distinction is sharper than any other case here, because the payload did
    /// speak about that row and what it said was "this is gone". Letting it rescue its own unmentioned
    /// parent would be a row protecting a container from beyond its own grave.
    ///
    /// Rejected entries stay excluded along with every other named id, deliberately. The mod
    /// <i>reported</i> those rows as present; the batch declined to write what it said about them,
    /// which is a very different claim from "they are gone". Deleting a row because the upsert
    /// describing it was malformed would turn every per-instance rejection into a data-loss event.
    /// </summary>
    private static Dictionary<ItemInstanceId, ItemInstance> ComputeSweep(
        ApplySnapshotCommand request,
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> instances,
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> scopeRows,
        IReadOnlySet<ItemInstanceId> deletedInstanceIds,
        IReadOnlyDictionary<ItemInstanceId, SnapshotRejectionReason> rejections)
    {
        var named = new HashSet<ItemInstanceId>(request.Upserts.Select(x => x.InstanceId));
        named.UnionWith(request.Deletes.Select(x => x.InstanceId));

        // Rule 4's "mentioned" set — see the doc comment for why it is exactly this narrow, and note the
        // two ways it is narrower than `named`.
        //
        // A REJECTED entry confers no authority (review round 2). Its id stays in `named`, so the row it
        // names is still protected from the sweep — that rule is unchanged and deliberate: the mod
        // reported the row as present, and the batch merely declined to write what it said. But an entry
        // the batch failed to write must not get to speak for OTHER rows. The sharp case is a
        // staff-tombstoned crate: the upsert naming it is rejected RemovedByStaff and the crate itself
        // correctly survives, yet before this the same id still unlocked rule 4 and every one of its
        // children was swept — the tombstone honoured for the container and ignored for its contents.
        // Same principle as an explicitly deleted row not rescuing its parent: a row the batch did not
        // write does not get a vote about its children.
        var mentionedContainers = new HashSet<ItemInstanceId>(named.Where(x => !rejections.ContainsKey(x)));

        // ...and the scope container counts as mentioned ONLY on a Container-scoped batch (review round
        // 2's HIGH). ApplySnapshotCommand carries both companion id fields and the endpoint previously
        // validated only that the *required* one for the declared kind was present — so a Character-scoped
        // batch could carry a stray `scope.containerInstanceId` naming any crate at all, land it here
        // ungated, and unlock rule 4 for a container the payload never mentioned. One extra JSON field
        // turned "nothing swept" into "that crate's entire contents deleted", under the guard's
        // thresholds and therefore silently. AnchorsInScope gates both of its scope comparisons on
        // ScopeKind for exactly this reason; this add now does too. (The endpoint also rejects a scope
        // carrying the other kind's id outright, since a scope naming two anchors is malformed and
        // should never have parsed — but the gate here is the correctness fix and stands alone.)
        if (request.ScopeKind == SnapshotScopeKind.Container
            && request.ScopeContainerInstanceId is { } mentionedScopeContainer)
        {
            mentionedContainers.Add(mentionedScopeContainer);
        }

        // The post-diff view the anchor walk resolves parents through. scopeRows is already overlaid
        // with the batch's own loaded copies; the batch's set is unioned in on top so a chain that
        // leaves the scope's stored membership (exactly what rule 3 exists to notice) can still be
        // followed to whatever this batch just made true.
        var view = new Dictionary<ItemInstanceId, ItemInstance>(scopeRows);
        foreach (var (id, row) in instances)
        {
            view[id] = row;
        }

        var sweep = new Dictionary<ItemInstanceId, ItemInstance>();
        foreach (var (id, row) in scopeRows)
        {
            // Rules 1 and 2, and the payload's own claims.
            if (named.Contains(id) || row.PendingSpawn || row.RemovedByStaff)
            {
                continue;
            }

            // Rule 3: this row's membership is stored, pre-diff; its parentage is post-diff. Where the
            // two disagree, the post-diff answer is the true one and the row has left.
            if (!AnchorsInScope(request, view, row))
            {
                continue;
            }

            // Rule 4: no claim of knowledge about the container means no claim about the inside.
            if (row.ParentKind == ParentKind.Container
                && (row.ContainerInstanceId is not { } containerId || !mentionedContainers.Contains(containerId)))
            {
                continue;
            }

            sweep[id] = row;
        }

        // Rule 5. Each round can only remove entries, so this shrinks monotonically and terminates; the
        // chain it walks is bounded by ItemInstance.MaxContainerDepth in any case. Survivors are scanned
        // from both the scope's own rows and the batch's loaded set, since a row the batch just moved
        // into one of these containers may sit outside the scope's read (it could have come from another
        // character's inventory in the same commit) and still protects its new parent.
        bool changed;
        do
        {
            changed = false;

            foreach (var row in scopeRows.Values.Concat(instances.Values))
            {
                if (sweep.ContainsKey(row.Id) || deletedInstanceIds.Contains(row.Id))
                {
                    continue;
                }

                if (row.ParentKind == ParentKind.Container
                    && row.ContainerInstanceId is { } parentId
                    && sweep.Remove(parentId))
                {
                    changed = true;
                }
            }
        }
        while (changed);

        return sweep;
    }

    /// <summary>
    /// Task 4, review round 1: whether <paramref name="row"/>'s container chain still terminates inside
    /// this batch's declared scope <i>after</i> the diff — walking the parent edges the three passes
    /// have already made true, not the stored ones the scope membership query answered from.
    ///
    /// A <c>Character</c> scope anchors when the chain ends on a row parented directly to the scope
    /// character. A <c>Container</c> scope anchors when the chain passes through the scope container
    /// itself (which is why it need not be present in <paramref name="view"/> — reaching its id is the
    /// answer).
    ///
    /// Every other termination is "no": a chain that ends on a different character, on the world, on a
    /// null container id, on a parent this batch cannot resolve, or that runs past
    /// <see cref="ItemInstance.MaxContainerDepth"/> or closes into a loop. That direction is deliberate
    /// and it is the safe one — an unresolvable answer here means "do not sweep this row", never "sweep
    /// it anyway". In practice the chain is always resolvable: for a <c>Character</c> scope every
    /// ancestor of a candidate carries the same denormalised <see cref="ItemInstance.RootCharacterId"/>
    /// and is therefore in the scope read, and for a <c>Container</c> scope the downward walk that built
    /// the scope necessarily visited every level between the row and the container.
    /// </summary>
    private static bool AnchorsInScope(
        ApplySnapshotCommand request,
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> view,
        ItemInstance row)
    {
        var visited = new HashSet<ItemInstanceId>();
        var cursor = row;

        for (var hop = 0; hop <= ItemInstance.MaxContainerDepth; hop++)
        {
            if (!visited.Add(cursor.Id))
            {
                return false;
            }

            if (cursor.ParentKind != ParentKind.Container)
            {
                return request.ScopeKind == SnapshotScopeKind.Character
                    && cursor.ParentKind == ParentKind.Character
                    && cursor.OwnerCharacterId is { } ownerCharacterId
                    && request.ScopeCharacterId is { } scopeCharacterId
                    && ownerCharacterId == scopeCharacterId;
            }

            if (cursor.ContainerInstanceId is not { } containerId)
            {
                return false;
            }

            if (request.ScopeKind == SnapshotScopeKind.Container
                && request.ScopeContainerInstanceId is { } scopeContainerId
                && containerId == scopeContainerId)
            {
                return true;
            }

            if (!view.TryGetValue(containerId, out var parent))
            {
                return false;
            }

            cursor = parent;
        }

        return false;
    }

    /// <summary>
    /// Walks <i>up</i> from every loaded container-parented row, pulling in ancestors the primary load
    /// didn't name, so the merged cycle/depth guard sees whole chains instead of stopping wherever the
    /// loaded set happens to end. Without it a loop running batch → stored → stored closes invisibly
    /// and gets written; with it the only chains that escape are ones deeper than the domain's own
    /// <see cref="ItemInstance.MaxContainerDepth"/> cap, which stored state cannot contain because
    /// every write path that produced it enforced the same cap.
    ///
    /// At most <see cref="ItemInstance.MaxContainerDepth"/> batched queries, each resolving a whole
    /// level at once — never one per instance, and independent of how large the batch is. In practice
    /// it stops after zero or one: a batch that names a container usually names its parents too.
    /// </summary>
    private async ValueTask LoadAncestorClosureAsync(Dictionary<ItemInstanceId, ItemInstance> instances, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < ItemInstance.MaxContainerDepth; hop++)
        {
            var missing = instances.Values
                .Where(x => x.ParentKind == ParentKind.Container
                    && x.ContainerInstanceId is { } parentId
                    && !instances.ContainsKey(parentId))
                .Select(x => x.ContainerInstanceId!.Value)
                .Distinct()
                .ToList();

            if (missing.Count == 0)
            {
                return;
            }

            var ancestors = await repository.LoadManyAsync(missing, cancellationToken);
            if (ancestors.Count == 0)
            {
                return;
            }

            foreach (var ancestor in ancestors)
            {
                instances.TryAdd(ancestor.Id, ancestor);
            }
        }
    }

    /// <summary>
    /// Walks <i>down</i> from <paramref name="seedInstanceIds"/> — every row this batch applied or
    /// deleted — pulling in whole subtrees a level at a time.
    ///
    /// This is what makes moving and deleting containers mean anything. <c>RootCharacterId</c> is the
    /// hot inventory read (<c>FindCarriedByRootCharacterAsync</c>), so a crate that changes hands
    /// while its contents keep the old value surfaces those contents in the <i>previous player's</i>
    /// inventory; and a child left behind by a deleted crate points at a row that no longer exists yet
    /// still answers that same read. Neither is reachable from the batch's own entries, because the
    /// mod is under no obligation to re-report the inside of a crate it merely moved.
    ///
    /// <c>TryAdd</c>, never an overwrite: a row this batch already loaded has been mutated in place by
    /// the diff above, and replacing it with a freshly-read copy would silently discard that. The
    /// already-queried set is what keeps a subtree that somehow contains a loop from spinning.
    /// </summary>
    private async ValueTask LoadDescendantClosureAsync(
        Dictionary<ItemInstanceId, ItemInstance> instances,
        IReadOnlySet<ItemInstanceId> seedInstanceIds,
        CancellationToken cancellationToken)
    {
        var queried = new HashSet<ItemInstanceId>();
        var frontier = seedInstanceIds.Where(queried.Add).ToList();

        for (var hop = 0; hop < ItemInstance.MaxContainerDepth && frontier.Count > 0; hop++)
        {
            var children = await repository.FindChildrenOfManyAsync(frontier, cancellationToken);
            frontier = [];

            foreach (var child in children)
            {
                instances.TryAdd(child.Id, child);
                if (queried.Add(child.Id))
                {
                    frontier.Add(child.Id);
                }
            }
        }
    }

    /// <summary>
    /// Soft-deletes everything nested inside a deleted row, transitively, and returns the full set of
    /// ids removed (the explicit deletes plus the cascade).
    ///
    /// The design spec settles the policy: soft-deleting a container soft-deletes its descendants, and
    /// a row whose <c>ContainerInstanceId</c> points at a deleted row must never be reachable. Without
    /// this a child of a deleted crate keeps its <c>RootCharacterId</c> and is still returned by the
    /// carried-inventory read, parented to a row that is gone.
    ///
    /// The parent edges walked here are <i>post-diff</i>, which is what makes this deterministic
    /// rather than an accident of statement ordering: a row this batch moved out of a doomed crate
    /// survives, and one it moved in goes with it, regardless of where either sat in the request
    /// arrays. Each cascaded row goes through the same <c>SoftDelete</c> as an explicit one, so it
    /// clears <see cref="ItemInstance.PendingSpawn"/> on the way out via <c>Patch()</c>+<c>Delete()</c>
    /// (ARCHITECTURE.md §9e gotcha 10) rather than a document replacement.
    ///
    /// Cascaded rows are deliberately absent from the response's <c>deleted</c> count — so that the
    /// caller's own arithmetic over its <c>deletes</c> array still closes — and reported in
    /// <c>cascadeDeleted</c> instead, so the number of rows that actually went away is never something
    /// the caller has to infer.
    /// </summary>
    private HashSet<ItemInstanceId> CascadeDeletes(
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> instances,
        IReadOnlySet<ItemInstanceId> deletedInstanceIds)
    {
        var removed = new HashSet<ItemInstanceId>(deletedInstanceIds);

        foreach (var instanceId in deletedInstanceIds)
        {
            if (instances.TryGetValue(instanceId, out var instance))
            {
                repository.SoftDelete(instance);
            }
        }

        if (removed.Count == 0)
        {
            return removed;
        }

        var childrenByContainer = new Dictionary<ItemInstanceId, List<ItemInstance>>();
        foreach (var instance in instances.Values)
        {
            if (instance.ParentKind == ParentKind.Container && instance.ContainerInstanceId is { } containerId)
            {
                if (!childrenByContainer.TryGetValue(containerId, out var siblings))
                {
                    siblings = [];
                    childrenByContainer[containerId] = siblings;
                }

                siblings.Add(instance);
            }
        }

        var frontier = new Queue<ItemInstanceId>(deletedInstanceIds);
        while (frontier.Count > 0)
        {
            var containerId = frontier.Dequeue();
            if (!childrenByContainer.TryGetValue(containerId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!removed.Add(child.Id))
                {
                    continue;
                }

                repository.SoftDelete(child);
                frontier.Enqueue(child.Id);
            }
        }

        return removed;
    }

    /// <summary>
    /// Whether a still-<see cref="ItemInstance.PendingSpawn"/> row may be touched by this batch at
    /// all: only from a <c>Character</c>-scoped batch naming the very character the row is owed to.
    ///
    /// That character has already been proven on the calling gameserver by step 9 (a scope character
    /// who isn't fails the whole batch as <c>WrongServer</c>), so this reuses a live
    /// <c>Character.CurrentServerId</c> check with no extra I/O — and it is the only check available,
    /// because a pending row's own <c>RootGameServerId</c> is null until delivery and therefore
    /// answers nothing.
    /// </summary>
    private static bool IsAdoptableByScope(ItemInstance stored, ApplySnapshotCommand request)
        => request.ScopeKind == SnapshotScopeKind.Character
            && request.ScopeCharacterId is { } scopeCharacterId
            && stored.RootCharacterId is { } rootCharacterId
            && rootCharacterId == scopeCharacterId;

    /// <summary>
    /// Recomputes <see cref="ItemInstance.RootCharacterId"/>, <see cref="ItemInstance.RootGameServerId"/>
    /// and <see cref="ItemInstance.ExpiresAt"/> for every loaded row and queues whatever writes that
    /// implies. All three travel together; the TTL is the easy one to forget and the one that breaks
    /// quietly — a crate dropped on the ground has to hand its ground TTL down to everything nested
    /// inside it, and take it back away when it is picked up.
    ///
    /// Two write shapes, both <b>targeted patches</b> and differing only in how wide the field list is
    /// (global constraint 6). A row this batch upserted goes through <c>WriteAppliedSnapshot</c>, over
    /// the whole surface a snapshot owns; a row it did <i>not</i> upsert — a descendant pulled in by
    /// the downward closure because the container above it moved — goes through
    /// <c>RewriteResolvedRoots</c>, over just these three derived fields.
    ///
    /// Neither is a whole-document write, and that is a correctness requirement rather than an
    /// optimisation. <c>ItemInstance</c> has no optimistic concurrency, so a document replacement
    /// writes back every field of whatever copy this batch happened to load: it resurrects a
    /// <c>PendingSpawn</c> flag another writer cleared (the phase 1 review's duplicated paid item),
    /// and — confirmed empirically, see
    /// <c>ItemInstanceRepositoryTests.Store_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_ResurrectsIt</c>
    /// — it undeletes a row another batch soft-deleted inside this batch's load-to-save window,
    /// returning a consumed item to the delivery queue still pending. A patch against that same row
    /// matches nothing and writes nothing, which is the outcome that is actually correct: the delete
    /// wins.
    /// </summary>
    private void WriteResolvedRoots(
        ApplySnapshotCommand request,
        WorldSettings settings,
        IReadOnlyDictionary<ItemId, ItemCatalogEntry> catalogEntries,
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> instances,
        IReadOnlySet<ItemInstanceId> appliedInstanceIds,
        IReadOnlySet<ItemInstanceId> removedInstanceIds,
        DateTimeOffset now)
    {
        var resolver = new RootResolver(
            instances,
            appliedInstanceIds,
            catalogEntries,
            request.GameServerId,
            TimeSpan.FromSeconds(settings.GroundItemTtlSeconds),
            now);

        foreach (var instance in instances.Values)
        {
            // A removed row is on its way out; rewriting its roots would only add a redundant write
            // and, for an applied-then-cascaded row, would put Store() and Delete() on the same id.
            if (removedInstanceIds.Contains(instance.Id))
            {
                continue;
            }

            var roots = resolver.Resolve(instance);

            if (appliedInstanceIds.Contains(instance.Id))
            {
                instance.RewriteResolvedRoots(roots.RootCharacterId, roots.RootGameServerId, roots.ExpiresAt, now);
                repository.WriteAppliedSnapshot(instance);
                continue;
            }

            var unchanged = Nullable.Equals(roots.RootCharacterId, instance.RootCharacterId)
                && Nullable.Equals(roots.RootGameServerId, instance.RootGameServerId)
                && Nullable.Equals(roots.ExpiresAt, instance.ExpiresAt);

            if (!unchanged)
            {
                repository.RewriteResolvedRoots(instance, roots.RootCharacterId, roots.RootGameServerId, roots.ExpiresAt, now);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="instance"/>'s denormalised delivery server is set and names some
    /// gameserver other than <paramref name="gameServerId"/>. A null <c>RootGameServerId</c> is not
    /// "another server" — it means the row has never been delivered anywhere yet.
    /// </summary>
    private static bool IsOnAnotherServer(ItemInstance instance, GameServerId gameServerId)
        => instance.RootGameServerId is { } rootGameServerId && rootGameServerId != gameServerId;

    /// <summary>
    /// Whether the stored row already says exactly what this upsert says, for the fields a snapshot
    /// carries. Only ever consulted at equal revisions: identical content is an ordinary no-op (the
    /// mod re-reporting what it already reported), while differing content at the same revision means
    /// two writers disagree about the instance's state at the same point in its history — an
    /// <see cref="SnapshotRejectionReason.IdentityConflict"/>, never a silent overwrite.
    ///
    /// Compares the typed fields directly rather than serialising both sides and comparing JSON:
    /// backend-owned fields (the delivery counters, the origin trio, the tombstone) are not part of
    /// what the mod reports and must not make an otherwise-identical snapshot look like a conflict.
    /// </summary>
    private static bool ContentMatches(ItemInstance stored, SnapshotUpsertRequest upsert)
    {
        if (stored.ParentKind != upsert.ParentKind)
        {
            return false;
        }

        var parentMatches = upsert.ParentKind switch
        {
            ParentKind.Character => Nullable.Equals(stored.OwnerCharacterId, upsert.ParentCharacterId) && stored.Slot == upsert.Slot,
            ParentKind.Container => Nullable.Equals(stored.ContainerInstanceId, upsert.ParentContainerInstanceId) && stored.Slot == upsert.Slot,
            ParentKind.World => Equals(stored.Transform, upsert.Transform),
            _ => false,
        };

        return parentMatches
            && Nullable.Equals(stored.Durability, upsert.Durability)
            && Nullable.Equals(stored.Ammo, upsert.Ammo)
            && AttributesMatch(stored.Attributes.Values, upsert.Attributes);
    }

    private static bool AttributesMatch(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> incoming)
    {
        if (stored.Count != incoming.Count)
        {
            return false;
        }

        foreach (var (key, value) in incoming)
        {
            if (!stored.TryGetValue(key, out var storedValue) || !string.Equals(storedValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks each instance up its container chain to whatever anchors it, memoising the answer so a
    /// crate with thirty things in it resolves once rather than thirty times.
    ///
    /// The rule for an anchor depends on whether this batch touched it. An anchor the batch
    /// <i>upserted</i> gets freshly derived roots: a character-parented one roots at that character on
    /// the calling gameserver (already guarded in step 9), a world-parented one has no owning character
    /// and takes a ground TTL only if the catalog classifies the item as
    /// <see cref="ItemPersistence.Despawns"/> — a persistent item (a parked vehicle, a placed
    /// deployable) is never swept. An anchor the batch left alone keeps the roots it already has:
    /// they were resolved correctly by whatever wrote them, and this batch has said nothing that would
    /// change them.
    ///
    /// That distinction is also what stops a batch from silently re-stamping an untouched row's
    /// <c>RootGameServerId</c> onto the calling server just because the row happened to be loaded as
    /// somebody's container.
    /// </summary>
    private sealed class RootResolver(
        IReadOnlyDictionary<ItemInstanceId, ItemInstance> instances,
        IReadOnlySet<ItemInstanceId> appliedInstanceIds,
        IReadOnlyDictionary<ItemId, ItemCatalogEntry> catalogEntries,
        GameServerId gameServerId,
        TimeSpan groundItemTtl,
        DateTimeOffset now)
    {
        private readonly Dictionary<ItemInstanceId, ResolvedRoots> _memo = [];

        public ResolvedRoots Resolve(ItemInstance instance) => Resolve(instance, []);

        private ResolvedRoots Resolve(ItemInstance instance, HashSet<ItemInstanceId> visiting)
        {
            if (_memo.TryGetValue(instance.Id, out var memoised))
            {
                return memoised;
            }

            // Belt and braces: the merged cycle guard above already rejects a batch that would create
            // one, but this walk must terminate even if a cycle somehow reached stored state, and
            // falling back to the row's current roots is the answer that changes nothing.
            if (!visiting.Add(instance.Id))
            {
                return Current(instance);
            }

            var resolved = instance.ParentKind switch
            {
                ParentKind.Container when instance.ContainerInstanceId is { } containerId
                    && instances.TryGetValue(containerId, out var container)
                    => Resolve(container, visiting),
                ParentKind.Character when appliedInstanceIds.Contains(instance.Id)
                    => new ResolvedRoots(instance.OwnerCharacterId, gameServerId, null),
                ParentKind.World when appliedInstanceIds.Contains(instance.Id)
                    => new ResolvedRoots(null, gameServerId, GroundExpiryFor(instance)),
                _ => Current(instance),
            };

            // An applied row must never be left rootless, and this is a second lock on the same door
            // rather than a tidy-up. Applying an upsert clears PendingSpawn, so the row becomes live;
            // a live row with a null RootGameServerId satisfies neither server guard — IsOnAnotherServer
            // is vacuous on null, and the pending-scope check no longer applies once the flag is gone —
            // which is exactly the state any gameserver in the hive could then world-parent onto its
            // own ground. The way in is inheritance: a Container-parented row anchored on a still-pending
            // ancestor this batch did not touch inherits that ancestor's null through Current(). The
            // container-parent guard blocks the one-hop form of that; this blocks every form, including
            // an ancestor further up than any entry in the batch names.
            //
            // The calling server is the right answer, not a fallback: the batch has just been told this
            // instance is physically present there, which is the same reasoning that lets an ack stamp
            // RootGameServerId at spawn time.
            if (resolved.RootGameServerId is null && appliedInstanceIds.Contains(instance.Id))
            {
                resolved = resolved with { RootGameServerId = gameServerId };
            }

            _memo[instance.Id] = resolved;
            return resolved;
        }

        /// <summary>The roots the row already carries — the right answer for an anchor this batch didn't touch, and the safe one for a chain it can't finish walking.</summary>
        private static ResolvedRoots Current(ItemInstance instance)
            => new(instance.RootCharacterId, instance.RootGameServerId, instance.ExpiresAt);

        /// <summary>Never trusts a gameserver clock for a TTL — always <paramref name="now"/> from the backend's own <c>TimeProvider</c>, per <see cref="ItemInstance.ExpiresAt"/>.</summary>
        private DateTimeOffset? GroundExpiryFor(ItemInstance instance)
            => catalogEntries.TryGetValue(instance.ItemId, out var entry) && entry.Persistence == ItemPersistence.Despawns
                ? now + groundItemTtl
                : null;
    }
}
