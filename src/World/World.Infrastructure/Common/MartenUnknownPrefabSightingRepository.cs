using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Inventory;
using Marten;
using Marten.Patching;
using Weasel.Postgresql.Tables;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>
/// Joins the shared <see cref="IWorldSession"/> like every other World repository, so a whole batch of
/// sightings queued via <see cref="RecordSighting"/> commits in one <see cref="SaveChangesAsync"/> —
/// one round trip regardless of how many sightings <c>POST /api/inventory/unknown-prefabs</c> carries.
/// </summary>
public sealed class MartenUnknownPrefabSightingRepository(IWorldSession worldSession) : IUnknownPrefabSightingRepository
{
    private readonly IDocumentSession _session = worldSession.Session;

    /// <summary>
    /// "Upsert... one round trip, no id lookup" (the task brief's own words), and this is what makes
    /// that literally true rather than aspirational.
    ///
    /// A single <c>Patch().Increment(...)</c> cannot do this alone: Marten silently no-ops a patch
    /// against a document id that does not exist yet (confirmed against live Postgres — no exception, no
    /// row created), so the very first sighting of a brand-new prefab would vanish rather than create a
    /// row. And a whole-document <see cref="IDocumentSession.Store"/> cannot either — global constraint
    /// 4, and precisely the bug Phase 1 already reproduced once for <c>ItemInstance</c>: two concurrent
    /// reports of the same prefab would each load-or-default the same starting count, increment it in
    /// memory, and the loser's <c>Store()</c> would silently overwrite the winner's, losing that
    /// increment with no error.
    ///
    /// Review round 1 also confirmed the three next-simplest alternatives are each independently wrong
    /// on Marten 9.23, not just less elegant: <c>Insert()</c> (throws <c>DocumentAlreadyExistsException</c>
    /// on the common "already-seen prefab" path and rolls back the *whole batch's* transaction, since
    /// every sighting in a batch shares this session); two separate <c>SaveChangesAsync</c> calls (an
    /// insert-attempt, then a patch) loses the single-transaction guarantee and can leave a phantom
    /// <c>Count = 0</c> row if the process dies between them; and there is no Marten 9.23
    /// insert-if-not-exists or patch-with-default primitive to reach for instead. Raw SQL bound to
    /// Marten's own table/serializer (below) is the correct mechanism here, not a shortcut around one.
    ///
    /// The fix queues two operations onto this call's shared session, <b>in this order</b> — load-bearing,
    /// see the inline comment below — both landing in one transaction/round trip via
    /// <see cref="SaveChangesAsync"/>:
    ///   1. A raw <c>INSERT ... ON CONFLICT (id) DO NOTHING</c> against Marten's own storage table,
    ///      seeding a baseline row (<c>Count = 0</c>) only if this prefab has never been reported before.
    ///      A no-op on every subsequent report, by construction.
    ///   2. An atomic <c>Patch().Increment(Count, count)</c> + <c>Patch().Set(LastSeenAt, now)</c>, which
    ///      now always has a row to land on — the one this call just seeded, or the one an earlier call
    ///      seeded.
    /// Verified against live Postgres, including 10 concurrent first-sightings of the same brand-new
    /// prefab racing this exact statement pair: <c>Count</c> landed as the exact sum of every increment
    /// in every run, no lost update, no exception — the <c>INSERT ... ON CONFLICT DO NOTHING</c>'s own
    /// atomicity is what closes the create-side race that a lookup-then-decide approach cannot.
    ///
    /// <see cref="UnknownPrefabSighting.FirstSeenAt"/> and <see cref="UnknownPrefabSighting.SampleContext"/>
    /// are seeded only by the INSERT and never patched afterward — "Increment count, update last-seen" is
    /// the brief's entire description of what a repeat report does, and no more.
    ///
    /// Trims <paramref name="prefabClassName"/> itself before deriving the id or storing it, rather than
    /// trusting the caller to have already done so (review round 1): <see cref="UnknownPrefabSighting.BuildId"/>
    /// trims internally, so a caller that forgot would still dedupe correctly, but would store a row whose
    /// <see cref="UnknownPrefabSighting.PrefabClassName"/> carries the untrimmed whitespace its own id
    /// disagrees with. There is currently exactly one caller (the endpoint's parse function, which already
    /// trims), so this was not a live bug — only a public interface that didn't say so.
    /// </summary>
    public void RecordSighting(string prefabClassName, int count, DateTimeOffset firstSeenAt, string? sampleContext, DateTimeOffset now)
    {
        var trimmedName = prefabClassName.Trim();
        var id = UnknownPrefabSighting.BuildId(trimmedName);

        var seed = new UnknownPrefabSighting
        {
            Id = id,
            PrefabClassName = trimmedName,
            Count = 0,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = now,
            SampleContext = sampleContext,
        };

        // Serialised through Marten's own configured serializer, not a hand-written anonymous object —
        // review round 1. The two produced identical JSON as of this SDK, so this is not fixing a live
        // bug either; it's removing a second, hand-maintained copy of "how does this document type
        // serialize" that only needs to drift once (a renamed `required` property poisons every row
        // written from that point on — a hand-built anonymous object would keep writing the *old* JSON
        // shape and Marten's own deserializer would then throw reading it back, 500ing the staff GET for
        // everyone until someone hand-writes SQL to fix the stored rows) to silently stop matching what
        // this document type actually persists.
        var seedJson = _session.DocumentStore.Options.Serializer().ToJson(seed);

        // The INSERT is queued strictly before both Patch calls, and that ordering is load-bearing, not
        // cosmetic (review round 1): Marten documents that operations of the *same* kind preserve queue
        // order, but says nothing about ordering *across* kinds (a raw SQL command versus a Patch). If
        // this INSERT were queued after the Patch calls instead, SaveChangesAsync would still succeed
        // with no exception — confirmed by reproducing the reversal directly — but a first-ever sighting
        // would patch a row that does not exist yet (silently ignored, per this method's own doc comment)
        // and only then get seeded at Count = 0, so every brand-new prefab's count would silently read
        // back 0 forever. RecordUnknownPrefabSightingsCommand_ForANewPrefab_RecordsTheSighting is this
        // ordering's regression test: it fails the moment these three statements are reordered.
        _session.QueueSqlCommand(
            $"insert into {TableName()} (id, data, mt_dotnet_type) values (?, ?::jsonb, null) on conflict (id) do nothing",
            id,
            seedJson);

        _session.Patch<UnknownPrefabSighting>(id).Increment(x => x.Count, count);
        _session.Patch<UnknownPrefabSighting>(id).Set(x => x.LastSeenAt, now);
    }

    public async ValueTask<IReadOnlyList<UnknownPrefabSighting>> FindForStaffAsync(
        int? minCount, DateTimeOffset? since, int offset, int limit, CancellationToken cancellationToken)
    {
        var query = _session.Query<UnknownPrefabSighting>().AsQueryable();

        if (minCount is { } min)
        {
            query = query.Where(x => x.Count >= min);
        }

        if (since is { } sinceValue)
        {
            query = query.Where(x => x.LastSeenAt >= sinceValue);
        }

        // Review round 1: Count/LastSeenAt alone leave ties unordered (Postgres makes no promise about
        // rows that compare equal on both), so a hard Take() could put a different subset of tied rows on
        // either side of the cut line between two calls with the same minCount/since/limit — silently
        // dropping or duplicating rows across pages, which is exactly what "paginated" is supposed to
        // rule out. Id is unique per row, so ThenBy(Id) gives the whole ordering a single deterministic
        // total order, making Skip/Take (offset/limit) together mean what they say.
        return await query
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.LastSeenAt)
            .ThenBy(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken) => await _session.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Marten's <c>mt_doc_&lt;lowercased type name&gt;</c> table for <see cref="UnknownPrefabSighting"/>,
    /// derived from Marten's own storage metadata rather than hand-formatted — the same idiom
    /// <c>WorldStoreTests.cs</c> already uses to cast <c>IDocumentStore.Options</c> to <c>StoreOptions</c>
    /// for schema inspection. Review round 1: a hand-formatted string silently drifts the day the type is
    /// renamed or given a <c>DocumentAlias</c> — Marten never drops the old table, so the raw INSERT below
    /// would keep succeeding against the orphaned one while both <c>Patch()</c> calls (which resolve the
    /// table through Marten's own metadata, not this string) target the new one and silently no-op. Every
    /// sighting would then read back missing or stuck at <c>Count = 0</c>, with no exception and no
    /// failing test that doesn't read a count back — confirmed by reproducing exactly that against a
    /// document store configured with a <c>DocumentAlias</c> before switching to this derivation.
    ///
    /// <b>Not cached (review round 2).</b> The round 1 version cached this in a <c>private static</c>
    /// field on a repository that is otherwise entirely per-scope, correct only by the accident that this
    /// module hardcodes one schema on one store — the reviewer's point was that a cache buys nothing
    /// measurable (this is a pure <see cref="StoreOptions"/> lookup with no database round trip, confirmed
    /// when this derivation was first probed) while adding an entire class of "which build/store is this
    /// value actually from" bug for a future reader to reason about. Removing the field removes the
    /// question rather than narrowing it.
    /// </summary>
    private string TableName()
        => ((StoreOptions)_session.DocumentStore.Options).Storage
            .FindFeature(typeof(UnknownPrefabSighting))
            .Objects.OfType<Table>().Single()
            .Identifier.QualifiedName;
}
