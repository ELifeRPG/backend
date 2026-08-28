namespace ELifeRPG.World.Domain.Snapshots;

/// <summary>
/// The monotonic sequence gate for one snapshot scope's <see cref="SnapshotMode.Full"/> reconciles
/// (task 3) — keyed on a string built by <see cref="BuildKey"/> (<c>Character:{id}</c> or
/// <c>Container:{id}</c>), deliberately never on <c>GameServerId</c>.
///
/// <b>Why per-scope, not per-server — recorded here so a later "simplification" does not re-litigate
/// it:</b> a single per-server counter would make every scope on that server serialize behind one
/// number, so an independent character's Full reconcile could be blocked by an unrelated container's.
/// Worse, a monotonic gate cannot be rewound: a single out-of-order arrival on a shared counter would
/// permanently discard every later, otherwise-valid batch behind it, for every scope sharing that one
/// counter — that is data loss dressed as deduplication, not the protection it looks like. Scoping the
/// cursor to exactly the thing the sequence numbers describe keeps one scope's ordering problem from
/// ever touching another's.
///
/// <c>Partial</c> batches need no cursor at all — they are ordered by each instance's own
/// <c>revision</c> instead (see <c>ApplySnapshotHandler</c>'s pass C) — so this document is written and
/// read only on the <c>Full</c> path; see <see cref="SnapshotMode.Full"/>'s own doc comment.
///
/// A plain Marten document, never a projection, and always replaced whole (<c>Store()</c>) rather than
/// patched: its entire content is exactly these two fields with no other writer to preserve, so the
/// targeted-patch-versus-whole-document-write distinction that matters for
/// <see cref="Items.ItemInstance"/> (never resurrect a concurrently-cleared flag) does not apply here.
///
/// <b>Optimistic concurrency is enabled for this document type alone</b> (fix round 1, item 7) — see
/// <c>World.Infrastructure/ServiceCollectionExtensions.cs</c> — which is what turns two <c>Full</c>
/// batches racing the same scope into one commit and one <c>ScopeCursorConflictException</c> rather
/// than a silent last-write-wins. This is deliberately not a Postgres row lock: a row lock adds a
/// deadlock edge this no-row-locks module has otherwise avoided everywhere, whereas an optimistic
/// conflict surfaces as the one <i>retryable</i> outcome <c>POST /api/inventory/snapshots</c> has —
/// exactly the right shape for two independently-valid batches that merely raced. Making this actually
/// work took two rounds against live Postgres, not one: fix round 1 confirmed a scope's very first,
/// "virgin" <c>Full</c> reconcile is covered too (Marten's check runs off its session-level identity
/// map, not the row's primary key), but its own fix — an internal re-read inside <c>AdvanceAsync</c> —
/// turned out to reopen the exact race this exists to close; fix round 2 removed it. See
/// <c>MartenScopeCursorRepository.AdvanceAsync</c>'s own doc comment for both findings' mechanics.
/// </summary>
public sealed class ScopeCursor
{
    /// <summary>
    /// Fix round 1, item 1: an absolute sanity ceiling on <c>sequence</c>, checked by
    /// <c>ApplySnapshotHandler</c> before any Postgres touch. Unlike <c>revision</c> (no upper bound —
    /// task 2's own reasoning, since a poisoned revision self-heals the moment a higher one arrives), a
    /// poisoned <c>sequence</c> can never self-heal: this cursor is monotonic and cannot be rewound, so
    /// a batch naming <c>long.MaxValue</c> would leave nothing that could ever compare strictly greater
    /// — a permanent denial of service on that one scope's entire <c>Full</c>-reconcile path. The value
    /// is a domain constant, not a <c>WorldSettings</c> knob: it is a sanity rail against a
    /// pathological input, not an operational tuning parameter, and it is set far above any realistic
    /// sequencing scheme (a plain incrementing counter, or a millisecond Unix epoch timestamp — 10^15 ms
    /// is roughly the year 33658) so no honest mod ever approaches it. (Known, accepted gap: a
    /// microsecond epoch or <c>DateTime.Ticks</c>-based scheme would exceed this and fail loudly,
    /// immediately, and non-destructively — a Bridge contract line, not a code change; see phase 3's
    /// <c>docs/bridge.md</c>.)
    ///
    /// Fix round 2, item 4 adds the symmetric lower bound (<c>sequence &lt; 0</c>, checked alongside
    /// this ceiling in <c>ApplySnapshotHandler</c>): a negative value self-heals the same way a
    /// negative <c>revision</c> would, so this is a reasoning-cost argument, not a correctness one — a
    /// bounds check that already exists should not have a documented-harmless asymmetric hole in it.
    /// </summary>
    public const long MaxSequence = 1_000_000_000_000_000L;

    /// <summary><c>Character:{characterId}</c> or <c>Container:{containerInstanceId}</c> — see <see cref="BuildKey"/>.</summary>
    public required string Id { get; init; }

    public required long LastAppliedSequence { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Builds this document's id from a snapshot batch's own declared scope. <see cref="SnapshotScopeKind"/>
    /// only ever names <c>Character</c> or <c>Container</c> (never <c>World</c> — see that enum's own
    /// doc comment), and the caller is expected to have already validated which companion id is present
    /// for the given kind — the same precondition every other scope-shaped check in this module already
    /// relies on <c>ApplySnapshotCommand</c>'s construction to have satisfied. A call that violates it is
    /// therefore unreachable from any request that passed the endpoint's own parsing — a programming
    /// error, not a business-rule violation a caller can trigger, so it is left to propagate as a bare
    /// <c>InvalidOperationException</c> (ARCHITECTURE.md §9e's "catch domain guard exceptions in the
    /// Application handler" rule reserves that catch-and-map treatment for exceptions representing an
    /// invariant a caller can actually reach), matching <c>AcknowledgeSpawnsCommand</c>'s own
    /// <c>"Unreachable AckKind"</c> fallback in this same module.
    /// </summary>
    public static string BuildKey(SnapshotScopeKind scopeKind, CharacterId? characterId, ItemInstanceId? containerInstanceId) => scopeKind switch
    {
        SnapshotScopeKind.Character when characterId is { } id => $"Character:{id.Value}",
        SnapshotScopeKind.Container when containerInstanceId is { } id => $"Container:{id.Value}",
        _ => throw new InvalidOperationException($"Cannot build a scope cursor key for {scopeKind} with no matching id."),
    };
}
