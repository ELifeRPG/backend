using System.Security.Cryptography;
using System.Text;

namespace ELifeRPG.World.Domain.Inventory;

/// <summary>
/// The staff promotion queue's raw material (task 5): a running tally of how many times the mod has
/// reported picking up/seeing an item whose prefab class name has no catalog entry — see
/// <c>ItemNotInCatalogException</c> and the design spec's "uncatalogued prefabs are not persisted".
/// Without this, staff have no way to learn which prefabs players actually encounter, and the catalog
/// never grows to match the world; this document is the entire feedback loop.
///
/// <b>Keyed on <see cref="BuildId"/>, a deterministic GUID of the prefab class name</b> — not a random
/// id looked up by name. That is what makes reporting the same prefab twice, from any call site, a
/// single point write (<c>session.Patch&lt;UnknownPrefabSighting&gt;(id)</c>) rather than a
/// find-or-create query, and it is load-bearing: every producer of this id must compute it exactly the
/// same way, or two reports of the same prefab silently create two rows instead of incrementing one.
/// <see cref="BuildId"/> is the sole producer for exactly this reason — see its own doc comment.
///
/// A plain Marten document, never a projection (global constraint 1, enforced by
/// <c>WorldStoreTests.EveryOtherWorldDocument_IsNotRegisteredAsAProjection</c> alongside
/// <c>ItemInstance</c>/<c>AppliedBatch</c>/<c>ScopeCursor</c>/<c>SuspiciousReconcile</c>): this is a
/// running tally with no history worth replaying, exactly like those four.
///
/// <see cref="Count"/> is only ever mutated by an atomic <c>Patch().Increment(...)</c>, never a whole-
/// document <c>Store()</c> — see <c>MartenUnknownPrefabSightingRepository.RecordSighting</c>'s own doc
/// comment for the reproduced concurrent-increment loss a naive load-increment-store would reintroduce
/// here (global constraint 4; Phase 1 already proved this bug once for <c>ItemInstance</c>).
/// </summary>
public sealed class UnknownPrefabSighting
{
    /// <summary>
    /// Structural cap on <see cref="PrefabClassName"/> — a public, rate-limited-but-unauthenticated-as-
    /// to-content write surface (the mod, not a human, populates this field) must not be allowed to
    /// store an unbounded string. Matches <c>ItemAttributes.MaxValueLength</c>'s own precedent value: a
    /// Reforger prefab resource path comfortably fits in a fraction of this, so 256 is generous headroom
    /// rather than a tight fit.
    /// </summary>
    public const int MaxPrefabClassNameLength = 256;

    /// <summary>
    /// Structural cap on <see cref="SampleContext"/> — a freeform diagnostic string the mod supplies
    /// (a location, an action, a stack of breadcrumbs) with no schema of its own. Bounded for the same
    /// reason as <see cref="MaxPrefabClassNameLength"/>: this field's whole existence is "the mod sends
    /// whatever it wants here," so nothing about it is otherwise limited.
    /// </summary>
    public const int MaxSampleContextLength = 512;

    /// <summary>
    /// Structural ceiling on how large a single reported <c>count</c> may be. Guards
    /// <see cref="Count"/> itself: an unbounded reported count feeds directly into an atomic
    /// <c>Increment</c>, so a corrupt or malicious value here is the one input on this write path that
    /// could otherwise push the stored counter toward <see cref="int"/> overflow. 1,000,000 is far above
    /// any realistic single-flush count a mod's local buffer would ever accumulate between reports.
    /// </summary>
    public const int MaxCountPerSighting = 1_000_000;

    public required Guid Id { get; init; }

    public required string PrefabClassName { get; init; }

    /// <summary>
    /// The running tally across every report this prefab has ever received, from any gameserver in the
    /// hive — sightings are hive-wide, matching every other inventory document (ARCHITECTURE.md §9e:
    /// "a hive needs to label," not isolate). Never written via <see cref="UnknownPrefabSighting"/>
    /// itself; see the class doc comment on why this is only ever patched.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// The earliest reported sighting of this prefab, set once when the row is first created and never
    /// overwritten afterward — see <c>MartenUnknownPrefabSightingRepository.RecordSighting</c>'s doc
    /// comment on why only <see cref="Count"/> and <see cref="LastSeenAt"/> are mutated on a repeat
    /// report.
    /// </summary>
    public required DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>The most recent report's server-received time — deliberately not mod-supplied; see <c>RecordSighting</c>'s doc comment.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>
    /// A representative example from the first report only (never overwritten by a later one, same
    /// reasoning as <see cref="FirstSeenAt"/>) — e.g. where the mod saw the prefab, or what action
    /// produced it. Optional: the mod may have nothing useful to say.
    /// </summary>
    public string? SampleContext { get; init; }

    /// <summary>
    /// The sole producer of this document's id — every call site that needs to address a prefab's
    /// sighting row (the write path, any future read keyed by name) must call this and only this, or
    /// two producers computing the id differently would silently create two rows for one prefab. Hashes
    /// the trimmed, UTF-8-encoded name with SHA-256 and takes the leading 16 bytes as the
    /// <see cref="Guid"/> — a deterministic, name-based id, chosen over a hand-rolled RFC 4122 UUIDv5
    /// because the BCL has no name-based UUID constructor as of this SDK (only
    /// <c>Guid.CreateVersion7</c>, which is time-based, not name-based).
    ///
    /// Review round 1: an earlier version of this used MD5 (matching this codebase's own test helper,
    /// <c>ELifeRPG.World.IntegrationTests.FixedCurrentGameServer.DeterministicGuid</c>, which stays on
    /// MD5 since it never leaves test code). <see cref="System.Security.Cryptography.MD5"/> throws on a
    /// FIPS-enforcing host and trips the CA5351 analyzer on a public, non-test write path — SHA-256 is a
    /// drop-in replacement for "deterministic, evenly-distributed bytes," which is all this needs; nothing
    /// here relies on any cryptographic property of either algorithm.
    ///
    /// The prefab class name is trimmed before hashing so that incidental leading/trailing whitespace
    /// from the wire never splits one prefab into two rows.
    /// </summary>
    public static Guid BuildId(string prefabClassName)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(prefabClassName.Trim()))[..16]);
}
