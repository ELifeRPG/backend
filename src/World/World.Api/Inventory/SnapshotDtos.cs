using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Api.Inventory;

/// <summary><c>POST /api/inventory/snapshots</c>'s request body — see the design spec's wire contract, verbatim minus <c>quantity</c> (nothing stacks).</summary>
public sealed record ApplySnapshotRequestDto
{
    public required Guid BatchId { get; init; }

    public required SnapshotScopeRequestDto Scope { get; init; }

    /// <summary>Required only when <see cref="Mode"/> is <c>Full</c> — see <see cref="SnapshotMode"/>.</summary>
    public long? Sequence { get; init; }

    /// <summary>One of <c>Partial</c> | <c>Full</c>, verbatim.</summary>
    public required string Mode { get; init; }

    public IReadOnlyList<SnapshotUpsertRequestDto> Upserts { get; init; } = [];

    public IReadOnlyList<SnapshotDeleteRequestDto> Deletes { get; init; } = [];
}

/// <summary>What part of the world this batch describes — one of <c>Character</c> | <c>Container</c>, verbatim. See <see cref="SnapshotScopeKind"/>.</summary>
public sealed record SnapshotScopeRequestDto
{
    public required string Kind { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <c>Character</c>.</summary>
    public Guid? CharacterId { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <c>Container</c>.</summary>
    public Guid? ContainerInstanceId { get; init; }
}

/// <summary>One entry of the batch's <c>upserts</c> array.</summary>
public sealed record SnapshotUpsertRequestDto
{
    public required Guid InstanceId { get; init; }

    /// <summary>The mod's own last-write-wins counter for this instance. Must be non-negative — see <c>SnapshotRejectionReason.ValueOutOfRange</c>.</summary>
    public required long Revision { get; init; }

    public required Guid ItemId { get; init; }

    public required SnapshotParentRequestDto Parent { get; init; }

    /// <summary>A 0..1 fraction. Anything outside that range is <c>ValueOutOfRange</c>.</summary>
    public float? Durability { get; init; }

    /// <summary>A magazine's round count — one integer on this row, never a container of rounds. Must be non-negative; deliberately not capped above, since a round count is trusted and monitored rather than enforced.</summary>
    public int? Ammo { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// One of three variants, discriminated by <see cref="Kind"/> (<c>Character</c> | <c>Container</c> |
/// <c>World</c>, verbatim — see <see cref="ELifeRPG.World.Domain.Items.ParentKind"/>): <c>Character</c>
/// requires <see cref="CharacterId"/>, <c>Container</c> requires <see cref="ContainerInstanceId"/>,
/// <c>World</c> requires <see cref="Transform"/>. <see cref="Slot"/> is only ever meaningful alongside
/// <see cref="CharacterId"/> or <see cref="ContainerInstanceId"/>.
/// </summary>
public sealed record SnapshotParentRequestDto
{
    public required string Kind { get; init; }

    public Guid? CharacterId { get; init; }

    public string? Slot { get; init; }

    public Guid? ContainerInstanceId { get; init; }

    public WorldTransformDto? Transform { get; init; }
}

/// <summary>One entry of the batch's <c>deletes</c> array.</summary>
public sealed record SnapshotDeleteRequestDto
{
    public required Guid InstanceId { get; init; }

    /// <summary>Must be non-negative. A delete naming a revision <i>lower</i> than the stored row's is rejected <c>StaleRevision</c> rather than skipped — unlike a stale upsert, a stale delete is destructive.</summary>
    public required long Revision { get; init; }

    /// <summary>One of <c>Consumed</c> | <c>Destroyed</c> | <c>Despawned</c> | <c>Traded</c> | <c>Unknown</c>, verbatim. See <see cref="DeleteReason"/>.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// <c>POST /api/inventory/snapshots</c>'s 200 response. <see cref="Applied"/>/<see cref="SkippedNoOp"/>/
/// <see cref="Deleted"/> are the real counts of what the batch changed in storage — how many upserts
/// were written, how many were discarded as a stale or identical revision, and how many of the
/// requested deletes were carried out. <see cref="ReplayOfPriorBatch"/> is <c>false</c> the first time a
/// <c>batchId</c> is applied and <c>true</c> every time it is replayed afterwards (task 3) — every other
/// field on a replay response is byte-identical to the original.
///
/// Each of those counts covers only entries the caller actually sent, so the arithmetic over its own
/// request closes. Exactly, for deletes:
/// <c>deleted + (rejected delete entries) == deletes.length</c>. Deleting a container also soft-deletes
/// everything nested inside it; those descendants are reported separately in
/// <see cref="CascadeDeleted"/> rather than folded into <see cref="Deleted"/>, so the request-shaped
/// arithmetic keeps working and the caller still learns how many rows actually went away.
///
/// For upserts the identity carries one further term:
/// <c>applied + skippedNoOp + (upserts the same batch deleted out from under) + (rejected upsert
/// entries) == upserts.length</c>. <see cref="Applied"/> counts upserts whose write <i>survived</i> the
/// batch, and an upsert whose row that same batch cascaded out of existence — it was moved into a
/// container the batch also deleted — is counted nowhere: not <see cref="Applied"/>, because nothing it
/// said survives (see <c>ApplySnapshotHandler</c>'s <c>appliedInstanceIds.ExceptWith(removedInstanceIds)</c>
/// and the reasoning at it), and not <see cref="Rejected"/>, because nothing about it was wrong. The row
/// itself is counted, in <see cref="CascadeDeleted"/>. This was documented as a clean three-term identity
/// until the phase 2 whole-branch review; the behaviour is correct and pinned by
/// <c>ApplySnapshotTests.ApplySnapshot_DeletingAContainerTheSameBatchMovesThingsIntoAndOutOf_ResolvesByPostBatchParentage</c>,
/// so it is the wording that was wrong.
/// </summary>
public sealed record ApplySnapshotResponseDto
{
    public required Guid BatchId { get; init; }

    public long? Sequence { get; init; }

    public required int Applied { get; init; }

    public required int SkippedNoOp { get; init; }

    public required int Deleted { get; init; }

    /// <summary>
    /// How many <i>additional</i> rows were soft-deleted as descendants of a deleted container —
    /// nothing the request named. <c>0</c> for a batch that deleted nothing, or deleted only empty
    /// containers and loose items.
    /// </summary>
    public required int CascadeDeleted { get; init; }

    /// <summary>
    /// How many rows a <c>mode: Full</c> reconcile soft-deleted purely for being <i>absent</i> from
    /// this payload — nothing the request named, and nothing nested inside anything it named. Always
    /// <c>0</c> for a <c>mode: Partial</c> batch, which says nothing about what it leaves out.
    /// Still-pending instances are never included: an instance the game has not spawned yet cannot be
    /// missing from a report of what the game can see.
    /// </summary>
    public required int Swept { get; init; }

    public required IReadOnlyList<SnapshotRejectionDto> Rejected { get; init; }

    public required bool ReplayOfPriorBatch { get; init; }

    public static ApplySnapshotResponseDto Create(ApplySnapshotResult.Applied source) => new()
    {
        BatchId = source.BatchId,
        Sequence = source.Sequence,
        Applied = source.AppliedCount,
        SkippedNoOp = source.SkippedNoOp,
        Deleted = source.Deleted,
        CascadeDeleted = source.CascadeDeleted,
        Swept = source.Swept,
        Rejected = source.Rejected.Select(SnapshotRejectionDto.Create).ToList(),
        ReplayOfPriorBatch = source.ReplayOfPriorBatch,
    };
}

/// <summary>One rejected instance, paired with why — one of <c>UnknownItem</c> | <c>UnknownInstance</c> | <c>StaleRevision</c> | <c>IdentityConflict</c> | <c>CycleDetected</c> | <c>AttributeLimit</c> | <c>NotOnThisServer</c> | <c>RemovedByStaff</c> | <c>ValueOutOfRange</c>.</summary>
public sealed record SnapshotRejectionDto
{
    public required Guid InstanceId { get; init; }

    public required string Reason { get; init; }

    public static SnapshotRejectionDto Create(SnapshotRejection source) => new()
    {
        InstanceId = source.InstanceId.Value,
        Reason = source.Reason.ToString(),
    };
}
