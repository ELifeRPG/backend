using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Snapshots;
using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>Same shape as <see cref="MartenAppliedBatchRepository"/>, joining the shared <see cref="IWorldSession"/> so <see cref="AdvanceAsync"/> commits inside the batch's own transaction.</summary>
public sealed class MartenScopeCursorRepository(IWorldSession worldSession) : IScopeCursorRepository
{
    private readonly IDocumentSession _session = worldSession.Session;

    public async ValueTask<ScopeCursor?> FindAsync(string scopeKey, CancellationToken cancellationToken)
        => await _session.LoadAsync<ScopeCursor>(scopeKey, cancellationToken);

    /// <summary>
    /// Deliberately a blind <c>Store()</c> — <b>no</b> internal <c>Load</c> here. Fix round 1 added one,
    /// reasoning it made this method "correct on its own"; fix round 2's review proved that reasoning
    /// backwards and it was removed. Both findings are empirical, against live Postgres, not inferred:
    ///
    /// <list type="bullet">
    /// <item><b>Why no internal <c>Load</c>:</b> re-reading here would <i>refresh</i> this session's
    /// tracked version to whatever is current in Postgres at write time — which is exactly wrong for a
    /// method whose whole job is to commit a decision (the sequence gate's stale-check) made earlier,
    /// against whatever the scope's cursor was <i>then</i>. Probed both ways on two batches racing one
    /// scope, A committing sequence 10 and B — whose own gate read happened before A's commit — advancing
    /// 7: <b>with</b> an internal <c>Load</c> here, B's <c>Store()</c> silently refreshes to A's version
    /// and succeeds, leaving the cursor at <b>7</b> — a lower sequence overwriting a higher one with no
    /// exception at all. <b>Without</b> it (this code), B's session still carries the version from its
    /// own earlier gate read, so Postgres's current version no longer matches what B's session last
    /// recorded, and B's <c>SaveChangesAsync</c> throws <c>ConcurrencyException</c> as it should — final
    /// cursor stays at <b>10</b>. See <c>ScopeCursorConcurrencyTests</c> for the deterministic
    /// non-overlapping-write-phase test this reasoning demands (a <c>Task.WhenAll</c>-driven race cannot
    /// see this bug, since both sides' writes land close enough together that neither refresh matters).</item>
    /// <item><b>Why this is still safe for the ordinary, no-race, sequential case:</b> every real caller
    /// (<c>ApplySnapshotHandler</c>'s own sequence gate, both the <c>Character</c> and <c>Container</c>
    /// halves) unconditionally calls <see cref="FindAsync"/> for this exact scope key earlier in the very
    /// same session before ever reaching this method — which is what gives this session a tracked
    /// version to begin with. Probed directly: gate <c>FindAsync</c>, then this blind <c>Store()</c>,
    /// sequential, no racer at all — throws nothing, cursor advances cleanly. A caller that skips that
    /// gate read and calls this directly is not a shape any production path produces; it is not this
    /// method's job to defend against it, and fix round 1's attempt to do so by re-reading here is what
    /// broke the real invariant instead.</item>
    /// <item>A session that has <i>never</i> looked at a given id at all still treats its own blind
    /// <c>Store()</c> as "I expect this row not to exist yet" — so two sessions racing a blind
    /// <c>Store()</c> of the <i>same, brand-new</i> id (the "virgin scope" case a poisoned first-ever
    /// <c>Full</c> batch would hit) still resolves correctly, one winner and one
    /// <c>ConcurrencyException</c>, through Marten's session-level identity map rather than a raw
    /// Postgres primary-key collision — this part of fix round 1's finding held up under further review.</item>
    /// </list>
    /// </summary>
    public ValueTask AdvanceAsync(string scopeKey, long sequence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _session.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = sequence, UpdatedAt = now });
        return ValueTask.CompletedTask;
    }
}
