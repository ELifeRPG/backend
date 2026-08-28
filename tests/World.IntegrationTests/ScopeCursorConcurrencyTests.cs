using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Snapshots;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Fix round 1, item 7 (optimistic
/// concurrency on <see cref="ScopeCursor"/>) went through two rounds against live Postgres before the
/// mechanics were right, and both rounds' findings are pinned here rather than left implicit:
///
/// <list type="bullet">
/// <item><b>Round 1:</b> a "virgin" scope key — never <c>Store</c>d before, by anyone — resolves the
/// same way an already-existing one does: through Marten's own session-level identity-map check, which
/// treats a session that has <c>Load</c>ed (or tried to) an id as knowing its current version, whatever
/// that version turns out to be. It is <b>not</b> a raw Postgres primary-key collision, which was the
/// a-priori guess this suite exists to test and rule out. See
/// <see cref="TwoSessions_BothAdvancingAVirginScopeKey_TheSecondToSaveThrowsScopeCursorConflict"/>.</item>
/// <item><b>Round 1's own fix was wrong, and round 2 corrected it.</b> Round 1 made
/// <c>MartenScopeCursorRepository.AdvanceAsync</c> re-<c>Load</c> the scope key internally, reasoning
/// this made the method "correct on its own." That re-read instead <i>refreshes</i> the calling
/// session's tracked version to whatever is current at write time — which silently lets a decision made
/// against a <i>stale</i> read (the sequence gate, evaluated earlier) commit anyway, overwriting a
/// fresher value with an older one and throwing nothing. See
/// <see cref="Repository_ALoserWhoseWritePhaseNeverOverlapsTheWinners_StillConflicts"/>, which is exactly
/// the shape a <c>Task.WhenAll</c>-driven race (both writes landing close together) cannot exercise.
/// Round 2 removed the internal <c>Load</c>; correctness now rests entirely on every real caller
/// (<c>ApplySnapshotHandler</c>'s sequence gate) already calling <see cref="IScopeCursorRepository.FindAsync"/>
/// for the same key earlier in the same session — proven directly in
/// <see cref="Repository_SequentialFindThenAdvanceAcrossTwoRequests_BothSucceed"/>.</item>
/// </list>
///
/// The `TwoSessions_*` tests are deterministic by construction rather than relying on
/// <c>Task.WhenAll</c> timing: two sessions each <c>Load</c> the scope key first, establishing what a
/// genuine interleaved race would see, and only then save in a fixed order — so the "loser" always
/// exists and always fails the same way, with no dependency on real thread scheduling. They pin raw
/// Marten behaviour directly against a session, not this module's own exception translation; the
/// `Repository_*` tests below them exercise that translation (<see cref="ScopeCursorConflictException"/>,
/// not the raw <c>JasperFx.ConcurrencyException</c>) through the real
/// <c>IScopeCursorRepository</c>/<c>IItemInstanceRepository.SaveChangesAsync</c> path instead — a gap
/// fix round 1 left open (its own translation bug, the <c>DocType</c> full-name fix, had no deterministic
/// regression guard; only a real <c>Task.WhenAll</c> race happened to catch it). <c>ApplySnapshotTests</c>
/// has the separate, necessarily timing-dependent end-to-end test that exercises the handler's own
/// mapping to <c>ApplySnapshotResult.ConcurrentReconcile</c>.
/// </summary>
public sealed class ScopeCursorConcurrencyTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task TwoSessions_BothAdvancingAVirginScopeKey_TheSecondToSaveThrowsScopeCursorConflict()
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        var scopeKey = $"Character:{Guid.NewGuid()}";

        await using var session1 = store.LightweightSession();
        await using var session2 = store.LightweightSession();

        // Both sessions "see" this key — as not yet existing — before either writes. This is what a
        // genuine interleaved race looks like, reproduced deterministically rather than by hoping two
        // real threads happen to overlap.
        Assert.Null(await session1.LoadAsync<ScopeCursor>(scopeKey));
        Assert.Null(await session2.LoadAsync<ScopeCursor>(scopeKey));

        session1.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = 1, UpdatedAt = DateTimeOffset.UtcNow });
        session2.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = 2, UpdatedAt = DateTimeOffset.UtcNow });

        await session1.SaveChangesAsync();

        // Asserted by exception type name, not a direct `catch (JasperFx.ConcurrencyException)` /
        // `using JasperFx;` — this file names the vendor exception exactly once, here, deliberately
        // loosely coupled to it.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => session2.SaveChangesAsync());
        Assert.Equal("JasperFx.ConcurrencyException", exception.GetType().FullName);

        await using var check = store.QuerySession();
        var final = await check.LoadAsync<ScopeCursor>(scopeKey);
        Assert.NotNull(final);
        Assert.Equal(1, final.LastAppliedSequence);
    }

    [Fact]
    public async Task TwoSessions_BothAdvancingAnAlreadyEstablishedScopeKey_TheSecondToSaveThrowsScopeCursorConflict()
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        var scopeKey = $"Character:{Guid.NewGuid()}";

        await using (var seed = store.LightweightSession())
        {
            seed.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = 1, UpdatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var session1 = store.LightweightSession();
        await using var session2 = store.LightweightSession();

        Assert.NotNull(await session1.LoadAsync<ScopeCursor>(scopeKey));
        Assert.NotNull(await session2.LoadAsync<ScopeCursor>(scopeKey));

        // Fresh instances, never the loaded reference — ScopeCursor is init-only, so a real "advance"
        // can only ever build a new object, never mutate the one Load returned. Proves the version
        // check tracks by id through the session's identity map, not by object reference.
        session1.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = 2, UpdatedAt = DateTimeOffset.UtcNow });
        session2.Store(new ScopeCursor { Id = scopeKey, LastAppliedSequence = 3, UpdatedAt = DateTimeOffset.UtcNow });

        await session1.SaveChangesAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => session2.SaveChangesAsync());
        Assert.Equal("JasperFx.ConcurrencyException", exception.GetType().FullName);

        await using var check = store.QuerySession();
        var final = await check.LoadAsync<ScopeCursor>(scopeKey);
        Assert.NotNull(final);
        Assert.Equal(2, final.LastAppliedSequence);
    }

    /// <summary>
    /// Fix round 2, item 2 — the test the reviewer asked for, whose write phases deliberately do
    /// <b>not</b> overlap: the "loser" reads the scope key <i>before</i> the "winner"'s entire request
    /// runs to completion and commits, and only afterwards does the loser attempt its own write. A
    /// <c>Task.WhenAll</c>-style race (both reads, then both writes, close together in time) cannot
    /// produce this shape, because it never lets one side fully finish before the other reads.
    ///
    /// This is exactly the case round 1's internal-<c>Load</c> "fix" got backwards: with that re-read in
    /// place, the loser's <c>Store()</c> at write time would pick up the winner's already-committed
    /// version and succeed, silently regressing the cursor to the loser's lower sequence. Without it
    /// (current code), the loser's session still carries only what it knew from its own earlier read,
    /// so Postgres's current version no longer matches and the write is correctly rejected.
    /// </summary>
    [Fact]
    public async Task Repository_ALoserWhoseWritePhaseNeverOverlapsTheWinners_StillConflicts()
    {
        var scopeKey = $"Character:{Guid.NewGuid()}";

        await using var loserScope = _provider.CreateAsyncScope();
        var loserCursors = loserScope.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
        var loserRepository = loserScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();

        // The loser's own "gate" read on a virgin scope — exactly ApplySnapshotHandler's own shape,
        // and the moment its sequence decision is effectively made.
        Assert.Null(await loserCursors.FindAsync(scopeKey, CancellationToken.None));

        // The winner's entire request now runs to completion and commits, in a wholly separate
        // scope/session — no overlap in time with the loser's read above, and none with the loser's
        // write below either.
        await using (var winnerScope = _provider.CreateAsyncScope())
        {
            var winnerCursors = winnerScope.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
            var winnerRepository = winnerScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            await winnerCursors.FindAsync(scopeKey, CancellationToken.None);
            await winnerCursors.AdvanceAsync(scopeKey, 10, DateTimeOffset.UtcNow, CancellationToken.None);
            await winnerRepository.SaveChangesAsync(CancellationToken.None);
        }

        // The loser now advances using only what its own earlier read established. Must be rejected —
        // through this module's own mapped exception, not the raw Marten one, since this goes through
        // the real IItemInstanceRepository.SaveChangesAsync translation.
        await loserCursors.AdvanceAsync(scopeKey, 7, DateTimeOffset.UtcNow, CancellationToken.None);
        await Assert.ThrowsAsync<ScopeCursorConflictException>(() => loserRepository.SaveChangesAsync(CancellationToken.None).AsTask());

        await using var checkScope = _provider.CreateAsyncScope();
        var checkCursors = checkScope.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
        var final = await checkCursors.FindAsync(scopeKey, CancellationToken.None);
        Assert.NotNull(final);
        // The winner's higher sequence must survive untouched — never silently overwritten by the
        // loser's lower one. This is the exact regression round 1's internal Load would reintroduce.
        Assert.Equal(10, final.LastAppliedSequence);
    }

    /// <summary>
    /// Fix round 1, item 5 (round 2's numbering) — a deterministic regression guard for this module's
    /// own exception <i>translation</i>, not just raw Marten behaviour: both sides read first (the
    /// shape <see cref="TwoSessions_BothAdvancingAVirginScopeKey_TheSecondToSaveThrowsScopeCursorConflict"/>
    /// pins at the session level), but through the real repository and its real
    /// <c>IItemInstanceRepository.SaveChangesAsync</c>, asserting the mapped
    /// <see cref="ScopeCursorConflictException"/> — the exact type the <c>DocType</c> full-name bug fix
    /// round 1 introduced and fixed, which nothing deterministic previously verified.
    /// </summary>
    [Fact]
    public async Task Repository_TwoScopesBothFindThenAdvance_TheSecondToSaveThrowsTheMappedScopeCursorConflictException()
    {
        var scopeKey = $"Character:{Guid.NewGuid()}";

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();

        var cursorsA = scopeA.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var cursorsB = scopeB.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
        var repositoryB = scopeB.ServiceProvider.GetRequiredService<IItemInstanceRepository>();

        Assert.Null(await cursorsA.FindAsync(scopeKey, CancellationToken.None));
        Assert.Null(await cursorsB.FindAsync(scopeKey, CancellationToken.None));

        await cursorsA.AdvanceAsync(scopeKey, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        await cursorsB.AdvanceAsync(scopeKey, 2, DateTimeOffset.UtcNow, CancellationToken.None);

        await repositoryA.SaveChangesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ScopeCursorConflictException>(() => repositoryB.SaveChangesAsync(CancellationToken.None).AsTask());
    }

    /// <summary>
    /// The corollary that makes <see cref="IScopeCursorRepository.FindAsync"/>-before-<see cref="IScopeCursorRepository.AdvanceAsync"/>
    /// a real precondition rather than an implementation detail: every production call site
    /// (<c>ApplySnapshotHandler</c>'s sequence gate) already satisfies it, and this proves that exact
    /// shape — repeated across two entirely separate, non-overlapping requests against the same scope —
    /// succeeds both times, with no internal <c>Load</c> inside <c>AdvanceAsync</c> to lean on.
    /// </summary>
    [Fact]
    public async Task Repository_SequentialFindThenAdvanceAcrossTwoRequests_BothSucceed()
    {
        var scopeKey = $"Character:{Guid.NewGuid()}";

        await using (var scope1 = _provider.CreateAsyncScope())
        {
            var repository = scope1.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var cursors = scope1.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
            await cursors.FindAsync(scopeKey, CancellationToken.None);
            await cursors.AdvanceAsync(scopeKey, 1, DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope2 = _provider.CreateAsyncScope())
        {
            var repository = scope2.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var cursors = scope2.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
            await cursors.FindAsync(scopeKey, CancellationToken.None);
            await cursors.AdvanceAsync(scopeKey, 2, DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var checkScope = _provider.CreateAsyncScope();
        var cursorsCheck = checkScope.ServiceProvider.GetRequiredService<IScopeCursorRepository>();
        var final = await cursorsCheck.FindAsync(scopeKey, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(2, final.LastAppliedSequence);
    }
}
