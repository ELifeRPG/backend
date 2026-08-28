using System.Runtime.CompilerServices;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 5's own "Tests"
/// bullet: a sighting is recorded, a repeat increments rather than duplicating, and the staff query
/// sorts and filters — plus review round 1's additions: an in-batch duplicate sums into one row, offset
/// paging reconstructs a stable set with no overlap/gap, and a genuine Count+LastSeenAt tie still orders
/// deterministically across repeated calls. Rate limiting is deliberately untested here — Task 6 owns
/// attaching the policy that makes it real; there is nothing on this endpoint today for a test to
/// assert against.
///
/// Every test uses a fresh, GUID-suffixed prefab class name (<see cref="UniquePrefabName"/>) rather
/// than relying on isolated storage per test: <see cref="UnknownPrefabSighting"/> rows are never
/// deleted, so this suite runs against the same accumulating Postgres data as every other run before
/// it, and the deterministic id (keyed on the name) means a shared name across test runs would silently
/// corrupt another run's counts. Tests that need to reason about the full contents of a time window
/// (offset paging, tie-breaking) additionally use a distinctly high count range so an unrelated test's
/// rows landing in the same `since` window can never interleave into the range under assertion.
/// </summary>
public sealed class UnknownPrefabSightingsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static string UniquePrefabName([CallerMemberName] string? caller = null) => $"{caller}_{Guid.NewGuid():N}";

    /// <summary>
    /// Every row this store holds for one prefab class name, read straight off the document store.
    ///
    /// The write-path tests below used to read the whole table (<c>FindForStaffAsync(limit: 10_000)</c>)
    /// and filter in memory, which was already living on borrowed time: sightings are never deleted, so
    /// this table accumulates roughly 150 rows per suite run and stood at 8,027 when this was written.
    /// Past 10,000 the read silently stops returning the row under test — it sorts by <c>Count</c>
    /// descending and these rows carry single-digit counts, so they are the first to fall off the end —
    /// and the failure would arrive as an <c>Assert.Single</c> "sequence contains no elements" on a test
    /// that had passed for months, on a suite that had already spent real time chasing one intermittent
    /// failure this round. Filtering in Postgres on the name each test already generated uniquely is
    /// exact, O(1) in the table's size, and cannot age out.
    ///
    /// Returns a list rather than the single row on purpose: two of the callers assert that a repeat
    /// report produced <b>one</b> row rather than two, which is the property the deterministic id
    /// exists for, so they need to see a second row if one is ever created.
    /// </summary>
    private async Task<IReadOnlyList<UnknownPrefabSighting>> RowsNamedAsync(string prefabClassName)
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();

        return await session.Query<UnknownPrefabSighting>()
            .Where(x => x.PrefabClassName == prefabClassName)
            .ToListAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecordUnknownPrefabSightingsCommand_ForANewPrefab_RecordsTheSighting()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var prefabName = UniquePrefabName();
        var firstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var result = await mediator.Send(new RecordUnknownPrefabSightingsCommand(
            [new UnknownPrefabSightingRequest(prefabName, 4, firstSeenAt, "near the docks")]));

        // Union case types are not real subclasses (ARCHITECTURE.md §9e's "genuine union type, not an
        // inheritance hierarchy") — result.GetType() is the union type itself, so this has to be a
        // pattern match rather than Assert.IsType, same idiom ApplySnapshotTests.cs uses throughout.
        if (result is not RecordUnknownPrefabSightingsResult.Recorded)
        {
            throw new InvalidOperationException($"Expected Recorded, got {result}");
        }

        var sighting = Assert.Single(await RowsNamedAsync(prefabName));

        // This assertion is also the regression test for RecordSighting's load-bearing statement
        // ordering (review round 1): reversing the queued INSERT and Patch calls still lets
        // SaveChangesAsync succeed with no exception, but silently leaves this at 0 instead of 4 — see
        // that method's own doc comment.
        Assert.Equal(4, sighting.Count);
        Assert.Equal(firstSeenAt, sighting.FirstSeenAt);
        Assert.Equal("near the docks", sighting.SampleContext);
    }

    /// <summary>
    /// The brief's own words: "Increment count, update last-seen." A repeat report of the same prefab
    /// must land as one row whose count is the sum of every report, never a second row — proving the
    /// deterministic id (<c>UnknownPrefabSighting.BuildId</c>) and the atomic-patch upsert
    /// (<c>MartenUnknownPrefabSightingRepository.RecordSighting</c>) actually compose the way both are
    /// documented to.
    /// </summary>
    [Fact]
    public async Task RecordUnknownPrefabSightingsCommand_ForARepeatedPrefab_IncrementsRatherThanDuplicating()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var prefabName = UniquePrefabName();
        var firstSeenAt = DateTimeOffset.UtcNow.AddHours(-1);

        await mediator.Send(new RecordUnknownPrefabSightingsCommand(
            [new UnknownPrefabSightingRequest(prefabName, 3, firstSeenAt, "first context")]));
        await mediator.Send(new RecordUnknownPrefabSightingsCommand(
            [new UnknownPrefabSightingRequest(prefabName, 5, firstSeenAt.AddMinutes(1), "second context")]));

        // One row, not two — the whole point of keying on a deterministic id.
        var sighting = Assert.Single(await RowsNamedAsync(prefabName));
        Assert.Equal(8, sighting.Count);

        // firstSeenAt/sampleContext are captured once, on the very first report, and never overwritten
        // by a later one — see UnknownPrefabSighting's own doc comment.
        Assert.Equal(firstSeenAt, sighting.FirstSeenAt);
        Assert.Equal("first context", sighting.SampleContext);
    }

    /// <summary>
    /// Review round 1: the same prefab named twice in one POST batch must sum into the one row, not
    /// create/patch two separate times in a way that only one survives. Distinct from the repeated-call
    /// test above — both entries here share one <c>RecordUnknownPrefabSightingsCommand</c> dispatch and
    /// therefore one shared session/SaveChangesAsync, which is the scenario
    /// <c>MartenUnknownPrefabSightingRepository.RecordSighting</c>'s doc comment on queuing multiple
    /// operations for the same id in one session is actually about.
    /// </summary>
    [Fact]
    public async Task RecordUnknownPrefabSightingsCommand_ForTheSamePrefabTwiceInOneBatch_SumsIntoOneRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var prefabName = UniquePrefabName();
        var now = DateTimeOffset.UtcNow;

        await mediator.Send(new RecordUnknownPrefabSightingsCommand(
        [
            new UnknownPrefabSightingRequest(prefabName, 3, now, "first"),
            new UnknownPrefabSightingRequest(prefabName, 4, now, "second"),
        ]));

        var sighting = Assert.Single(await RowsNamedAsync(prefabName));

        Assert.Equal(7, sighting.Count);
    }

    /// <summary>
    /// The concurrent-create race this design exists to survive (global constraint 4): several reports
    /// of a genuinely brand-new prefab landing at once must still sum to the exact total, never losing
    /// an increment to the classic load-increment-store race. Mirrors the live-Postgres probe run while
    /// designing <c>MartenUnknownPrefabSightingRepository.RecordSighting</c>, kept here as a permanent
    /// regression test.
    /// </summary>
    [Fact]
    public async Task RecordUnknownPrefabSightingsCommand_ConcurrentFirstSightingsOfTheSamePrefab_SumWithoutLosingAnIncrement()
    {
        var prefabName = UniquePrefabName();

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new RecordUnknownPrefabSightingsCommand(
                [new UnknownPrefabSightingRequest(prefabName, 1, DateTimeOffset.UtcNow, null)]));
        });

        await Task.WhenAll(tasks);

        var sighting = Assert.Single(await RowsNamedAsync(prefabName));

        Assert.Equal(10, sighting.Count);
    }

    [Fact]
    public async Task UnknownPrefabSightingsQuery_SortsByCountDescendingAndFiltersByMinCountAndSince()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var lowName = UniquePrefabName();
        var highName = UniquePrefabName();
        var highestName = UniquePrefabName();
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var now = DateTimeOffset.UtcNow;

        await mediator.Send(new RecordUnknownPrefabSightingsCommand([new UnknownPrefabSightingRequest(lowName, 2, now, null)]));
        await mediator.Send(new RecordUnknownPrefabSightingsCommand([new UnknownPrefabSightingRequest(highName, 50, now, null)]));
        await mediator.Send(new RecordUnknownPrefabSightingsCommand([new UnknownPrefabSightingRequest(highestName, 100, now, null)]));

        var results = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: 10, Since: since, Offset: null, Limit: 100));
        var names = results.Select(x => x.PrefabClassName).ToList();

        // minCount=10 excludes the 2-count row.
        Assert.DoesNotContain(lowName, names);
        Assert.Contains(highName, names);
        Assert.Contains(highestName, names);

        // Sorted by count descending: the 100-count row must sort ahead of the 50-count row.
        Assert.True(names.IndexOf(highestName) < names.IndexOf(highName));

        // since filters out everything reported before the cutoff — a cutoff set after all three
        // writes above must exclude all three.
        var afterCutoff = DateTimeOffset.UtcNow.AddSeconds(1);
        var noneAfterCutoff = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: null, Since: afterCutoff, Offset: null, Limit: 100));
        Assert.DoesNotContain(noneAfterCutoff, x => x.PrefabClassName == lowName || x.PrefabClassName == highName || x.PrefabClassName == highestName);
    }

    /// <summary>
    /// Review round 1: the query took only <c>limit</c>, so past the first page staff had no way to see
    /// the rest of the queue. Seeds a distinctly-countered, unambiguously-ordered block of rows and
    /// proves two consecutive pages (offset 0/limit 3, then offset 3/limit 3) are disjoint and together
    /// reconstruct the exact expected order with neither a gap nor an overlap — offset paging is only
    /// meaningful together with the stable ordering the next test covers.
    /// </summary>
    [Fact]
    public async Task UnknownPrefabSightingsQuery_WithOffset_ReturnsTheNextPageWithNoOverlapOrGap()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        // A distinctly high, strictly descending count range no other test in this suite uses, so an
        // unrelated row sharing this time window can never land inside the expected order below.
        var orderedNames = Enumerable.Range(0, 6).Select(_ => UniquePrefabName()).ToList();
        var sightings = orderedNames
            .Select((name, i) => new UnknownPrefabSightingRequest(name, 900_000 - i, DateTimeOffset.UtcNow, null))
            .ToList();

        await mediator.Send(new RecordUnknownPrefabSightingsCommand(sightings));

        var page1 = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: 800_000, Since: since, Offset: 0, Limit: 3));
        var page2 = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: 800_000, Since: since, Offset: 3, Limit: 3));

        var page1Names = page1.Select(x => x.PrefabClassName).ToList();
        var page2Names = page2.Select(x => x.PrefabClassName).ToList();

        Assert.Equal(3, page1Names.Count);
        Assert.Equal(3, page2Names.Count);
        Assert.Empty(page1Names.Intersect(page2Names));
        Assert.Equal(orderedNames, page1Names.Concat(page2Names).ToList());
    }

    /// <summary>
    /// Review round 1 raised, review round 2 attempted a predictive fix, review round 3 found <b>that</b>
    /// fix could still pass by chance and this is the corrected version.
    ///
    /// <c>Count</c>/<c>LastSeenAt</c> alone leave a genuine tie unordered, which would let a hard
    /// <c>Take</c> silently drop or duplicate a row between two calls with the same filters — exactly
    /// what "paginated" is supposed to rule out. Every sighting below rides the same
    /// <c>RecordUnknownPrefabSightingsCommand</c> dispatch, so <c>RecordUnknownPrefabSightingsHandler</c>
    /// stamps all of them with the identical server-received "now" — a genuine tie on both <c>Count</c>
    /// and <c>LastSeenAt</c> across the whole set, not a near-miss.
    ///
    /// <b>Why 8 tied rows, not 2 (review round 3).</b> The round 2 version of this test used exactly two
    /// tied rows and asserted the query's order matched a predicted permutation of them. That prediction
    /// is correct (see below), but with only two items a query returning tie order in <i>any</i> other
    /// way — including no total order at all, i.e. exactly the regression this test exists to catch — still
    /// has a coin-flip chance of landing on the one order this test predicts. Directly measured: with
    /// <c>.ThenBy(x =&gt; x.Id)</c> removed, 15 independent trials of the round 2 two-row version matched
    /// the prediction 9 times (60%) and matched plain insertion order only 5 times (33%) — confirming the
    /// tie order without the fix is not stably anything, and a two-item predictive test is barely better
    /// than the determinism-only version review round 1 shipped, which the reviewer already showed cannot
    /// fail at all. Asserting a specific permutation of 8 distinct items instead has only a
    /// 1-in-8-factorial (1-in-40,320) chance of accidentally matching whatever unspecified order an
    /// unfixed query happens to produce on any given run — confirmed below by rerunning this exact test
    /// with the tiebreaker removed and watching it fail on the first try.
    ///
    /// The prediction itself was probed against live Postgres before writing this: <c>Guid.ToByteArray(bigEndian: true)</c>
    /// compared lexicographically (via <see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>)
    /// matched Postgres's own <c>ORDER BY id ASC</c> in every one of 8 trials of 40 random ids each — that
    /// byte order is the RFC 4122 canonical (network/big-endian) representation, which is what Postgres's
    /// <c>uuid</c> type compares by.
    ///
    /// Verified this test actually catches the regression it exists for: removed <c>.ThenBy(x =&gt; x.Id)</c>
    /// locally, reran this test, watched it fail, then reverted — see the task report for that run's
    /// output.
    /// </summary>
    [Fact]
    public async Task UnknownPrefabSightingsQuery_ForAGenuineTieOnCountAndLastSeenAt_BreaksTheTieInPostgresUuidOrder()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Short, dedicated names rather than UniquePrefabName() (review round 3, nit): that helper
        // embeds this method's own (long) name, and xunit truncates a collection-diff element around
        // 60 chars — long enough here to make a future failure print nothing a person could use to
        // tell which of the eight rows was out of place. Still globally unique via the full GUID.
        var names = Enumerable.Range(0, 8).Select(i => $"Tie{i}_{Guid.NewGuid():N}").ToList();
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await mediator.Send(new RecordUnknownPrefabSightingsCommand(
            names.Select(name => new UnknownPrefabSightingRequest(name, 750_000, DateTimeOffset.UtcNow, null)).ToList()));

        var expectedOrder = names
            .OrderBy(name => UnknownPrefabSighting.BuildId(name).ToByteArray(bigEndian: true), ByteArrayComparer.Instance)
            .ToList();

        var results = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: 700_000, Since: since, Offset: null, Limit: 100));
        var actualOrder = results.Where(x => names.Contains(x.PrefabClassName)).Select(x => x.PrefabClassName).ToList();

        Assert.Equal(expectedOrder, actualOrder);
    }

    [Fact]
    public async Task RecordUnknownPrefabSightingsCommand_WithMoreSightingsThanMaxUnknownPrefabSightingsPerBatch_IsRejectedAsBatchTooLarge()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var settings = await mediator.Send(new WorldSettingsQuery());

        var now = DateTimeOffset.UtcNow;
        var tooMany = Enumerable.Range(0, settings.MaxUnknownPrefabSightingsPerBatch + 1)
            .Select(_ => new UnknownPrefabSightingRequest(UniquePrefabName(), 1, now, null))
            .ToList();

        var result = await mediator.Send(new RecordUnknownPrefabSightingsCommand(tooMany));

        if (result is not RecordUnknownPrefabSightingsResult.BatchTooLarge tooLarge)
        {
            throw new InvalidOperationException($"Expected BatchTooLarge, got {result}");
        }

        Assert.Equal(settings.MaxUnknownPrefabSightingsPerBatch + 1, tooLarge.Requested);
        Assert.Equal(settings.MaxUnknownPrefabSightingsPerBatch, tooLarge.Max);
    }

    /// <summary>Proves the clamp is real rather than vacuously true: seeds strictly more rows within the query's own window than the page size, so an unclamped query would provably return more than <see cref="WorldSettings.MaxUnknownPrefabQueryPageSize"/>.</summary>
    [Fact]
    public async Task UnknownPrefabSightingsQuery_WithLimitAboveMax_IsClampedToMaxUnknownPrefabQueryPageSize()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var settings = await mediator.Send(new WorldSettingsQuery());

        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var extra = settings.MaxUnknownPrefabQueryPageSize + 5;
        var sightings = Enumerable.Range(0, extra)
            .Select(_ => new UnknownPrefabSightingRequest(UniquePrefabName(), 1, DateTimeOffset.UtcNow, null))
            .ToList();

        await mediator.Send(new RecordUnknownPrefabSightingsCommand(sightings));

        var results = await mediator.Send(new UnknownPrefabSightingsQuery(MinCount: null, Since: since, Offset: null, Limit: extra + 1000));

        Assert.Equal(settings.MaxUnknownPrefabQueryPageSize, results.Count);
    }

    /// <summary>
    /// Review round 2: <c>offset</c> was floored at 0 but left unbounded above, letting a single request
    /// force Postgres to scan past an arbitrarily large number of rows via <c>Skip</c>. Exercises
    /// <see cref="UnknownPrefabSightingsHandler"/> directly against a stub repository (same idiom as
    /// <c>TestServices.cs</c>'s <c>FixedCurrentGameServer</c> — a plain hand-written test double, not a
    /// mocking library) rather than seeding <c>WorldSettings.MaxUnknownPrefabQueryOffset</c>'s real
    /// default (100,000) worth of rows just to prove an arithmetic clamp, which the real store neither
    /// needs nor could do cheaply.
    /// </summary>
    [Fact]
    public async Task UnknownPrefabSightingsHandler_WithOffsetAboveMax_ClampsToMaxUnknownPrefabQueryOffset()
    {
        var repository = new RecordingUnknownPrefabSightingRepository();
        var settings = new FixedWorldSettingsRepository(new WorldSettings { MaxUnknownPrefabQueryOffset = 5, MaxUnknownPrefabQueryPageSize = 10 });
        var handler = new UnknownPrefabSightingsHandler(repository, settings);

        await handler.Handle(new UnknownPrefabSightingsQuery(MinCount: null, Since: null, Offset: 999, Limit: null), CancellationToken.None);

        Assert.Equal(5, repository.LastOffset);
        // LastLimit isn't this test's focus, but leaving it recorded-and-unasserted was itself a nit
        // (review round 3) — a null Limit should still resolve to the page-size default independent of
        // the offset clamp under test.
        Assert.Equal(10, repository.LastLimit);
    }

    /// <summary>The calibration half: an offset already at or below the cap passes through unchanged.</summary>
    [Fact]
    public async Task UnknownPrefabSightingsHandler_WithOffsetAtOrBelowMax_PassesItThroughUnchanged()
    {
        var repository = new RecordingUnknownPrefabSightingRepository();
        var settings = new FixedWorldSettingsRepository(new WorldSettings { MaxUnknownPrefabQueryOffset = 5, MaxUnknownPrefabQueryPageSize = 10 });
        var handler = new UnknownPrefabSightingsHandler(repository, settings);

        await handler.Handle(new UnknownPrefabSightingsQuery(MinCount: null, Since: null, Offset: 3, Limit: null), CancellationToken.None);

        Assert.Equal(3, repository.LastOffset);
        Assert.Equal(10, repository.LastLimit);
    }
}

/// <summary>Records the <c>offset</c>/<c>limit</c> it was called with rather than touching Postgres — see the offset-clamp tests above.</summary>
internal sealed class RecordingUnknownPrefabSightingRepository : IUnknownPrefabSightingRepository
{
    public int? LastOffset { get; private set; }

    public int? LastLimit { get; private set; }

    public void RecordSighting(string prefabClassName, int count, DateTimeOffset firstSeenAt, string? sampleContext, DateTimeOffset now)
        => throw new NotSupportedException("Not exercised by the offset-clamp tests.");

    public ValueTask<IReadOnlyList<UnknownPrefabSighting>> FindForStaffAsync(
        int? minCount, DateTimeOffset? since, int offset, int limit, CancellationToken cancellationToken)
    {
        LastOffset = offset;
        LastLimit = limit;
        return ValueTask.FromResult<IReadOnlyList<UnknownPrefabSighting>>([]);
    }

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by the offset-clamp tests.");
}

/// <summary>
/// An in-memory <see cref="IWorldSettingsRepository"/>. Named for its original use (handing the
/// offset-clamp tests a fixed set of settings with no database), and now also the seam
/// <c>WorldSettingsTests</c> exercises <c>UpdateWorldSettingsHandler</c>'s bounds and partial-update
/// semantics through — that handler's whole job is read-modify-validate-write, and doing it against
/// this rather than Postgres keeps a bounds test off the shared settings singleton every other World
/// integration test reads from.
/// </summary>
internal sealed class FixedWorldSettingsRepository(WorldSettings settings) : IWorldSettingsRepository
{
    public WorldSettings Current { get; private set; } = settings;

    public int UpsertCount { get; private set; }

    public ValueTask<WorldSettings> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);

    public ValueTask UpsertAsync(WorldSettings updated, CancellationToken cancellationToken)
    {
        Current = updated;
        UpsertCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Lexicographic <c>byte[]</c> comparison — <c>byte[]</c> has no built-in <see cref="IComparable{T}"/>,
/// and this has to match Postgres's own <c>uuid</c> byte-order comparison exactly for the tie-break
/// prediction test above to mean anything. Probed against live Postgres before use: sorting by
/// <c>Guid.ToByteArray(bigEndian: true)</c> through this comparer matched Postgres's own
/// <c>ORDER BY id ASC</c> in every one of 8 trials of 40 random ids each.
/// </summary>
internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y) => ((ReadOnlySpan<byte>)x!).SequenceCompareTo(y!);
}
