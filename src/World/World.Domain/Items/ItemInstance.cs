using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// One concrete, persisted entity — a rifle, a bandage, a magazine, a crate. Reforger has no item
/// stacking (see the design spec's engine evidence), so a row is always exactly one entity: there is
/// no <c>Quantity</c> here, and there must never be one. A magazine's round count is the one quantity
/// the engine tracks, and it lives in <see cref="Ammo"/> as a plain integer on this same row.
///
/// A plain Marten document, never a projection — see Global Constraint 1 of
/// docs/superpowers/plans/2026-08-26-world-inventory-phase-1.md. Under a snapshot/last-write-wins
/// model the game sends a full set rather than a delta, and full-world persistence eventually
/// requires pruning, which would permanently break projection rebuild if this were event-sourced.
///
/// <see cref="Origin"/>, <see cref="OriginRef"/> and <see cref="RegisteredAt"/> are immutable after
/// creation, and that is a compile error to violate, not just a convention: all three use
/// <c>private init</c> rather than <c>private set</c>. An <c>init</c> accessor can only be assigned
/// inside an object initializer at the point of construction — <see cref="Register"/> is the only
/// place that does this — and is rejected by the compiler everywhere else, including from a future
/// method added inside this very class (e.g. a snapshot-apply that wholesale-copies fields onto an
/// existing instance). Every other mutable property here still uses <c>private set</c>, so no code
/// outside this class can write to any of them either way; the extra <c>init</c> restriction on
/// these three specifically closes the "future mutator forgets and reassigns them" hole that a plain
/// private setter cannot. System.Text.Json (via <c>UseSystemTextJsonWithPrivateSetters()</c>) still
/// deserializes <c>init</c> accessors on load — verified by
/// <c>ItemInstanceRepositoryTests.Store_ThenSaveChanges_PersistsEveryFieldNeededToReconstructTheInstance</c>.
/// </summary>
public sealed class ItemInstance
{
    /// <summary>
    /// Containers may nest at most this deep. A direct-on-character or direct-in-world item is depth
    /// 0; each container step below that is +1. A domain constant, not a runtime setting — see
    /// Controller ruling 1 in the phase 1 task brief: this is a structural cap already baked into
    /// stored data, so making it tunable at runtime would let a settings edit retroactively
    /// invalidate rows that were valid when written.
    /// </summary>
    public const int MaxContainerDepth = 6;

    public ItemInstanceId Id { get; private set; }

    public ItemId ItemId { get; private set; }

    /// <summary>
    /// The LWW key the mod bumps on any change to this instance; see the design spec's correctness
    /// core. Backend-minted rows always start at 0.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>Null means "use the catalog's DisplayName" — this exists for the day a player can rename a weapon or label a crate.</summary>
    public string? DisplayNameOverride { get; private set; }

    public ParentKind ParentKind { get; private set; }

    public CharacterId? OwnerCharacterId { get; private set; }

    public ItemInstanceId? ContainerInstanceId { get; private set; }

    public string? Slot { get; private set; }

    /// <summary>Only meaningful when <see cref="ParentKind"/> is <see cref="Items.ParentKind.World"/>.</summary>
    public WorldTransform? Transform { get; private set; }

    /// <summary>
    /// Denormalised delivery server. Null until delivery — the delivery server is resolved at spawn
    /// time from <c>Character.CurrentServerId</c>, never stamped at grant time (a portal purchase has
    /// no server to stamp, and stamping the purchasing server would pin the item to a map the player
    /// may not be on when they next join).
    /// </summary>
    public GameServerId? RootGameServerId { get; private set; }

    /// <summary>
    /// Denormalised owning character, resolved through however many containers separate this
    /// instance from the character actually holding it. The load-bearing field that turns a
    /// character's nested inventory into one indexed query instead of a recursive walk — see the
    /// design spec's "RootCharacterId is the load-bearing schema decision" note.
    /// </summary>
    public CharacterId? RootCharacterId { get; private set; }

    /// <summary>
    /// Ground TTL. Null for a persistent item, or for anything not currently world-parented. Never
    /// trust a gameserver clock for this — it is always computed from the backend clock passed in.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public float? Durability { get; private set; }

    /// <summary>
    /// A magazine's round count — one instance, one integer, never a container of rounds. Trusted and
    /// monitored, never enforced (see the design spec's "Magazine ammo" section).
    /// </summary>
    public int? Ammo { get; private set; }

    public ItemAttributes Attributes { get; private set; } = ItemAttributes.Empty;

    public ExternalRef? ExternalRef { get; private set; }

    /// <summary>Immutable after creation — see the class summary.</summary>
    public ItemOrigin Origin { get; private init; }

    /// <summary>Immutable after creation — see the class summary.</summary>
    public OriginRef? OriginRef { get; private init; }

    /// <summary>
    /// True from the moment this row is minted until the mod acks it. Pending-spawn rows are never
    /// reconciled away — see the design spec's correctness core, mechanism 2.
    /// </summary>
    public bool PendingSpawn { get; private set; }

    /// <summary>A sticky tombstone: once true, a later upsert of this id is rejected, never resurrected.</summary>
    public bool RemovedByStaff { get; private set; }

    /// <summary>
    /// Backend-owned count of how many times this row was served in a pending-delivery payload.
    /// Never touched by the mod, never conflated with <see cref="Revision"/>.
    /// </summary>
    public int DeliveryAttempts { get; private set; }

    /// <summary>
    /// The reason given on the most recent <c>POST /api/inventory/instances/{id}/spawn-failed</c>
    /// call against this row, if any. Null until the first negative ack. See
    /// <see cref="RecordSpawnFailure"/> — this is what lets the staff queue
    /// (<c>GET /api/inventory/undeliverable</c>) show *why* a delivery failed, not just that it did.
    /// </summary>
    public SpawnFailureReason? LastSpawnFailureReason { get; private set; }

    /// <summary>When <see cref="LastSpawnFailureReason"/> was last recorded.</summary>
    public DateTimeOffset? LastSpawnFailureAt { get; private set; }

    /// <summary>
    /// How many times a negative ack has been reported against this row. Distinct from
    /// <see cref="DeliveryAttempts"/> (which counts how many times the row was *served*, incremented
    /// by the pending-delivery read regardless of what the mod does with it) — this counts how many
    /// times the mod has actively reported failure.
    /// </summary>
    public int SpawnFailureCount { get; private set; }

    /// <summary>Immutable after creation — see the class summary.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Assembles the union-ish read view over this instance's parent-shaped fields. See
    /// <see cref="ItemParent"/> for why this is never the persisted shape.
    /// </summary>
    public ItemParent Parent => new(ParentKind, OwnerCharacterId, ContainerInstanceId, Slot, Transform);

    /// <summary>
    /// Mints a fresh, character-owned instance — the shape every grant (shop purchase, gathering,
    /// staff grant, provisioning) produces. Always <see cref="PendingSpawn"/>, always
    /// <see cref="Revision"/> 0, always <see cref="RootGameServerId"/> null: see the field doc
    /// comments above for why. This is the only place <see cref="Origin"/>, <see cref="OriginRef"/>
    /// and <see cref="RegisteredAt"/> are ever assigned.
    /// </summary>
    public static ItemInstance Register(
        ItemInstanceId id,
        ItemId itemId,
        CharacterId ownerCharacterId,
        ItemOrigin origin,
        OriginRef? originRef,
        DateTimeOffset now)
    {
        return new ItemInstance
        {
            Id = id,
            ItemId = itemId,
            Revision = 0,
            ParentKind = ParentKind.Character,
            OwnerCharacterId = ownerCharacterId,
            RootCharacterId = ownerCharacterId,
            RootGameServerId = null,
            Attributes = ItemAttributes.Empty,
            Origin = origin,
            OriginRef = originRef,
            PendingSpawn = true,
            RemovedByStaff = false,
            DeliveryAttempts = 0,
            RegisteredAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Reparents this instance directly onto a character, clearing any ground TTL — an item on a
    /// character's person never despawns. <paramref name="rootGameServerId"/> is optional because a
    /// domain-only caller (e.g. a unit test) may not have a server context; the application layer
    /// supplies it from <c>Character.CurrentServerId</c> when this represents a real pickup.
    /// </summary>
    public void MoveToCharacter(CharacterId ownerCharacterId, string? slot, DateTimeOffset now, GameServerId? rootGameServerId = null)
    {
        ParentKind = ParentKind.Character;
        OwnerCharacterId = ownerCharacterId;
        ContainerInstanceId = null;
        Slot = slot;
        Transform = null;
        RootCharacterId = ownerCharacterId;
        RootGameServerId = rootGameServerId;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Reparents this instance into the world (dropped, or spawned as loot). <paramref name="despawns"/>
    /// is supplied by the caller from the catalog's <c>Persistence</c> classification — the domain
    /// does not call out to the catalog itself, per the phase 1 brief. Only <c>Despawns</c> items get
    /// a TTL; a <c>Persistent</c> item (a parked vehicle, a placed deployable) gets <c>null</c> and is
    /// never swept.
    /// </summary>
    public void MoveToWorld(WorldTransform transform, bool despawns, TimeSpan groundItemTtl, DateTimeOffset now, GameServerId? rootGameServerId = null)
    {
        ParentKind = ParentKind.World;
        OwnerCharacterId = null;
        ContainerInstanceId = null;
        Slot = null;
        Transform = transform;
        RootCharacterId = null;
        RootGameServerId = rootGameServerId;
        ExpiresAt = despawns ? now + groundItemTtl : null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Reparents this instance into a container, rejecting a cycle (this instance would become its
    /// own ancestor — <see cref="ContainerCycleException"/>) or a nesting depth beyond
    /// <see cref="MaxContainerDepth"/> (<see cref="ContainerDepthExceededException"/>).
    /// <paramref name="resolveContainer"/> is the caller's in-memory lookup over whatever instances
    /// are already loaded for this operation — see the design spec's write-path mechanics: the whole
    /// point is one batched load, never a per-hop round trip.
    ///
    /// Root fields (<see cref="RootCharacterId"/>, <see cref="RootGameServerId"/>) and
    /// <see cref="ExpiresAt"/> are pulled from the target container, which is exactly how a nested
    /// chain resolves to its owning character (or inherits a dropped crate's TTL) with no recursion
    /// at read time.
    ///
    /// This only updates <c>this</c> instance. If the container being moved (rather than the one
    /// it's moving into) already has its own descendants, calling this does not cascade to them —
    /// their <see cref="RootCharacterId"/>, <see cref="RootGameServerId"/> and <see cref="ExpiresAt"/>
    /// stay exactly as they were, now stale, until whatever batch operation moved this container also
    /// rewrites them. That cascade is an application-layer concern over a loaded set of instances
    /// (see the design spec's write-path mechanics), not something this method can do on its own —
    /// it only ever sees the one instance it's called on.
    /// </summary>
    public void MoveToContainer(ItemInstanceId containerInstanceId, string? slot, Func<ItemInstanceId, ItemInstance> resolveContainer, DateTimeOffset now)
    {
        ResolveDepthOrThrow(containerInstanceId, resolveContainer);

        var container = resolveContainer(containerInstanceId);

        ParentKind = ParentKind.Container;
        ContainerInstanceId = containerInstanceId;
        Slot = slot;
        OwnerCharacterId = null;
        Transform = null;
        RootCharacterId = container.RootCharacterId;
        RootGameServerId = container.RootGameServerId;
        ExpiresAt = container.ExpiresAt;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records that this row was served in a pending-delivery payload (task 4's
    /// <c>GET /api/inventory/characters/{characterId}/pending</c>) — see the design spec's mechanism 5
    /// (the delivery cap). Backend-owned and strictly separate from <see cref="Revision"/>: the mod
    /// never sends this value, and it is never treated as a last-write-wins key. Once
    /// <see cref="DeliveryAttempts"/> reaches <c>WorldSettings.MaxDeliveryAttempts</c> the read that
    /// would otherwise call this stops offering the row at all (see
    /// <c>IItemInstanceRepository.FindPendingByRootCharacterAsync</c>), so this method itself enforces
    /// no cap — it only ever runs on a row the query already decided is still eligible.
    /// </summary>
    public void RecordDeliveryAttempt(DateTimeOffset now)
    {
        DeliveryAttempts++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records a negative ack (<c>POST /api/inventory/instances/{id}/spawn-failed</c>) — called by
    /// <c>SpawnFailedHandler</c> regardless of whether the row is still under
    /// <c>WorldSettings.MaxDeliveryAttempts</c> or already undeliverable, since the reason is exactly
    /// what tells staff whether a redelivery will help (<see cref="SpawnFailureReason.InventoryFull"/>)
    /// or won't (<see cref="SpawnFailureReason.PrefabMissing"/>,
    /// <see cref="SpawnFailureReason.AdoptionUnsupported"/>). Deliberately never touches
    /// <see cref="PendingSpawn"/> or <see cref="DeliveryAttempts"/> — both stay owned entirely by the
    /// pending-delivery read, see that field's own doc comment.
    /// </summary>
    public void RecordSpawnFailure(SpawnFailureReason reason, DateTimeOffset now)
    {
        LastSpawnFailureReason = reason;
        LastSpawnFailureAt = now;
        SpawnFailureCount++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Clears <see cref="PendingSpawn"/> and stamps the resolved delivery server — called by the ack
    /// path (<c>AcknowledgeSpawnsHandler</c>) the moment the mod reports a granted instance as
    /// successfully spawned. <see cref="RootGameServerId"/> is deliberately resolved here, at ack
    /// time, from whichever gameserver made the call (already validated by that handler's server
    /// guard against <c>Character.CurrentServerId</c>) — never at grant time; see this field's own
    /// doc comment for why. The handler only ever calls this on the <c>PendingSpawn</c> →
    /// <c>false</c> transition (its <c>Cleared</c> outcome); a replayed ack for an already-cleared row
    /// (<c>AlreadyCleared</c>) never reaches here, so this is not itself idempotency-guarded.
    /// </summary>
    public void AcknowledgeSpawn(GameServerId gameServerId, DateTimeOffset now)
    {
        PendingSpawn = false;
        RootGameServerId = gameServerId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Mints a child instance for an engine-spawned entity nested inside a granted prefab — a rifle's
    /// magazine, a phone's SIM — declared in the <c>children</c> array of an ack
    /// (<c>POST /api/inventory/acks</c>). These entities have no backend id of their own and are not
    /// splits (Reforger has no stacks), so the ack is the only way they can ever persist; see the
    /// design spec's "Delivering a granted instance". Always parented to <paramref name="parent"/> as
    /// a <see cref="Items.ParentKind.Container"/> child, inheriting its
    /// <see cref="RootCharacterId"/>/<see cref="RootGameServerId"/> exactly the way
    /// <see cref="MoveToContainer"/> does. Never <see cref="PendingSpawn"/> — the entity already
    /// exists in the game by construction, so there is nothing left to deliver.
    ///
    /// Idempotency (never minting a second child for the same parent+slot on a replayed ack) is
    /// <c>AcknowledgeSpawnsHandler</c>'s responsibility, not this factory's — it only ever gets called
    /// once a slot is confirmed not already minted.
    /// </summary>
    public static ItemInstance RegisterChild(ItemInstanceId id, ItemId itemId, ItemInstance parent, string slot, DateTimeOffset now)
    {
        return new ItemInstance
        {
            Id = id,
            ItemId = itemId,
            Revision = 0,
            ParentKind = ParentKind.Container,
            ContainerInstanceId = parent.Id,
            Slot = slot,
            RootCharacterId = parent.RootCharacterId,
            RootGameServerId = parent.RootGameServerId,
            Attributes = ItemAttributes.Empty,
            Origin = ItemOrigin.EngineSpawnedChild,
            OriginRef = new OriginRef("World", parent.Id.Value.ToString()),
            PendingSpawn = false,
            RemovedByStaff = false,
            DeliveryAttempts = 0,
            RegisteredAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Clears <see cref="PendingSpawn"/> as part of an explicit delete of this row — see the design
    /// spec's worked example C ("the granted item is consumed before it is acked") and the phase 1
    /// task brief: <see cref="PendingSpawn"/> only ever protects a row from *reconcile* (its absence
    /// from a snapshot), never from the mod explicitly saying "this is gone". The common cause is a
    /// granted item consumed before its ack lands; leaving the flag set here would re-spawn a
    /// legitimately-consumed item at the character's next login. Callers combine this with an actual
    /// soft-delete of the same row in the same unit of work — see
    /// <c>IItemInstanceRepository.SoftDelete</c> — this method only clears the in-memory flag so the
    /// cleared value is what gets written.
    /// </summary>
    public void ClearPendingOnExplicitDelete(DateTimeOffset now)
    {
        PendingSpawn = false;
        UpdatedAt = now;
    }

    /// <summary>
    /// Walks from <paramref name="containerInstanceId"/> up through its own container chain,
    /// throwing <see cref="ContainerCycleException"/> the moment this instance's own id reappears
    /// (which is exactly what "this instance would become its own ancestor" looks like from the
    /// target's side, whether the cycle is direct or transitive), and
    /// <see cref="ContainerDepthExceededException"/> if the resulting depth would exceed
    /// <see cref="MaxContainerDepth"/>.
    /// </summary>
    private void ResolveDepthOrThrow(ItemInstanceId containerInstanceId, Func<ItemInstanceId, ItemInstance> resolveContainer)
    {
        var depth = 1;
        var visited = new HashSet<ItemInstanceId> { Id };
        var currentId = containerInstanceId;

        while (true)
        {
            if (!visited.Add(currentId))
            {
                throw new ContainerCycleException(Id, containerInstanceId);
            }

            var current = resolveContainer(currentId);
            if (current.ParentKind != ParentKind.Container)
            {
                break;
            }

            depth++;
            currentId = current.ContainerInstanceId!.Value;
        }

        if (depth > MaxContainerDepth)
        {
            throw new ContainerDepthExceededException(Id, containerInstanceId, depth, MaxContainerDepth);
        }
    }
}
