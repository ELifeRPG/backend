using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using JasperFx;
using Marten;
using Marten.Patching;
using Npgsql;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>
/// Joins the shared <see cref="IWorldSession"/> rather than owning a session of its own — see
/// <see cref="WorldSession"/> for why World needs one unit of work per scope instead of one per
/// repository.
/// </summary>
public sealed class MartenItemInstanceRepository : IItemInstanceRepository
{
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Explicit name for the partial unique index on (ContainerInstanceId, Slot) — see
    /// ServiceCollectionExtensions.cs — so <see cref="SaveChangesAsync"/> can check
    /// <c>PostgresException.ConstraintName</c> and translate only *this* violation into
    /// <see cref="ChildSlotAlreadyMintedException"/>. Review round 2, item (ii): translating on
    /// SqlState alone caught every unique violation on this document — the ExternalRef index, or a
    /// stray schema-migration collision (exactly the kind this project hit once, per the phase 1 task
    /// brief's environment note on a first run against a fresh volume) — under a misleading type.
    /// </summary>
    internal const string ChildSlotUniqueIndexName = "world_iteminstance_child_slot_uidx";

    private readonly IDocumentSession _session;
    private readonly TimeProvider _timeProvider;
    private readonly IItemCatalogResolver _catalogResolver;

    public MartenItemInstanceRepository(IWorldSession worldSession, TimeProvider timeProvider, IItemCatalogResolver catalogResolver)
        : this(worldSession.Session, timeProvider, catalogResolver)
    {
    }

    /// <summary>
    /// Used only by <c>MartenItemInstanceParticipant</c> for cross-module atomic writes — the
    /// session is already bound to a shared transaction the caller owns. Mirrors
    /// <c>MartenBankAccountRepository</c>'s internal constructor exactly, except this module never
    /// needs the raw transaction itself: grants take no row lock (see <see cref="GrantAsync"/>), so
    /// there is nothing here that needs it the way <c>FetchForUpdateAsync</c> does.
    /// </summary>
    internal MartenItemInstanceRepository(IDocumentSession session, TimeProvider timeProvider, IItemCatalogResolver catalogResolver)
    {
        _session = session;
        _timeProvider = timeProvider;
        _catalogResolver = catalogResolver;
    }

    public async ValueTask<ItemInstance?> FindByIdAsync(ItemInstanceId id, CancellationToken cancellationToken)
        // Query(), not LoadAsync(). Both compile and both take the strongly-typed id (ARCHITECTURE.md
        // §9e gotcha 4), but LoadAsync is a direct id fetch that bypasses the soft-delete filter
        // SoftDeletedWithIndex() applies to every Query<> read on this document — so it returned rows
        // an explicit delete had already removed, while every other read on this repository did not.
        // See IItemInstanceRepository.FindByIdAsync's doc comment for what that cost.
        => await _session.Query<ItemInstance>().Where(x => x.Id == id).SingleOrDefaultAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<ItemInstance>> FindByRootCharacterAsync(CharacterId rootCharacterId, CancellationToken cancellationToken)
        // Compares the unwrapped Guid, not the nullable strongly-typed id directly: Marten's LINQ
        // provider can't parameterize a CharacterId? as a Uuid column value — same reasoning as
        // MartenBankAccountRepository.FindByCharacterIdAsync (ARCHITECTURE.md §9e gotcha 4).
        => await _session.Query<ItemInstance>()
            .Where(x => x.RootCharacterId != null && x.RootCharacterId!.Value.Value == rootCharacterId.Value)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<ItemInstance>> FindCarriedByRootCharacterAsync(CharacterId rootCharacterId, DateTimeOffset now, CancellationToken cancellationToken)
        // Same nullable-strongly-typed-id unwrap as FindByRootCharacterAsync above (Marten's LINQ
        // provider can't parameterize a CharacterId? directly). Soft-deleted rows are already excluded
        // by Marten's default query behaviour under SoftDeletedWithIndex() — no explicit clause needed.
        //
        // !PendingSpawn is the "holds" side of the holds-versus-owed split with FindPendingByRootCharacterAsync:
        // a row still awaiting its first spawn+ack is owed, not held, and belongs only in the pending
        // read — see that method's doc comment. Fixed per phase 1 review round 1: this clause was
        // missing in the original cut, which made every not-yet-acked row double-spawn (once from
        // /items, once from /pending, since nothing in Phase 1 clears PendingSpawn before task 5's ack
        // path exists).
        => await _session.Query<ItemInstance>()
            .Where(x => x.RootCharacterId != null && x.RootCharacterId!.Value.Value == rootCharacterId.Value)
            .Where(x => !x.PendingSpawn)
            .Where(x => !x.RemovedByStaff)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<ItemInstance>> FindPendingByRootCharacterAsync(
        CharacterId rootCharacterId,
        int limit,
        int maxDeliveryAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await _session.Query<ItemInstance>()
            .Where(x => x.RootCharacterId != null && x.RootCharacterId!.Value.Value == rootCharacterId.Value)
            .Where(x => x.PendingSpawn)
            .Where(x => !x.RemovedByStaff)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > now)
            .Where(x => x.DeliveryAttempts < maxDeliveryAttempts)
            .OrderBy(x => x.RegisteredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<ItemInstance>> LoadManyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // Compare the strongly-typed id itself, never `x.Id.Value` — Marten's LINQ provider rejects
        // the latter outright. See ARCHITECTURE.md §9e gotcha 4 and MartenItemRepository.FindByIdsAsync.
        var idArray = ids.ToArray();
        return await _session.Query<ItemInstance>().Where(x => x.Id.IsOneOf(idArray)).ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ItemInstance>> FindChildrenOfManyAsync(
        IReadOnlyList<ItemInstanceId> containerInstanceIds, CancellationToken cancellationToken)
    {
        if (containerInstanceIds.Count == 0)
        {
            return [];
        }

        // A third variation on ARCHITECTURE.md §9e gotchas 4/7, worth spelling out because neither of
        // the two existing recipes is what works here. Equality against a nullable strongly-typed id
        // has to unwrap all the way to the raw Guid (FindByRootCharacterAsync:
        // `x.RootCharacterId!.Value.Value == characterId.Value`), so the obvious analogue was
        // `x.ContainerInstanceId!.Value.Value.IsOneOf(guidArray)`. That compiles and then throws at
        // runtime: `Unable to cast object of type 'System.Guid[]' to type
        // 'IEnumerable<ItemInstanceId>'`. IsOneOf resolves the *member* being filtered rather than the
        // expression's static type, collapses `.Value.Value` back to the ItemInstanceId member, and
        // then demands the array in that member's own type. So this unwraps exactly one level — to
        // ItemInstanceId, not to Guid — and passes ItemInstanceId[]. The explicit null check is still
        // required (gotcha 7).
        //
        // No !RemovedByStaff filter here, deliberately, unlike FindChildrenAsync — see this method's
        // doc comment on IItemInstanceRepository.
        var containerIds = containerInstanceIds.Distinct().ToArray();
        return await _session.Query<ItemInstance>()
            .Where(x => x.ContainerInstanceId != null && x.ContainerInstanceId!.Value.IsOneOf(containerIds))
            .ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ItemInstance>> FindChildrenAsync(ItemInstanceId containerInstanceId, CancellationToken cancellationToken)
        // Same nullable-strongly-typed-id unwrap as FindByRootCharacterAsync — Marten's LINQ provider
        // can't parameterize a nullable ItemInstanceId directly.
        //
        // !RemovedByStaff matches every other read in this repository, and it is load-bearing here
        // rather than cosmetic: this method's only consumer is AcknowledgeSpawnsHandler's idempotency
        // cache, which matches an existing child by slot and hands its id straight back as Minted.
        // Without the filter, a replayed ack would find a staff-tombstoned child and tell the mod to
        // adopt it — the sticky tombstone silently undone from the read side. (Whole-branch review, I3.)
        => await _session.Query<ItemInstance>()
            .Where(x => x.ContainerInstanceId != null && x.ContainerInstanceId!.Value.Value == containerInstanceId.Value)
            .Where(x => !x.RemovedByStaff)
            .ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<ItemInstance>> FindUndeliverableAsync(int maxDeliveryAttempts, CancellationToken cancellationToken)
        => await _session.Query<ItemInstance>()
            .Where(x => x.PendingSpawn)
            .Where(x => !x.RemovedByStaff)
            .Where(x => x.DeliveryAttempts >= maxDeliveryAttempts)
            .OrderBy(x => x.RegisteredAt)
            .ToListAsync(cancellationToken);

    public void Store(ItemInstance instance) => _session.Store(instance);

    // Patch, never Store — see IItemInstanceRepository.WriteAcknowledgedSpawn's doc comment. Three
    // fields, matching ItemInstance.AcknowledgeSpawn exactly; an UPDATE that matches no row because a
    // snapshot delete got there first correctly writes nothing rather than resurrecting a consumed
    // item.
    public void WriteAcknowledgedSpawn(ItemInstance instance)
        => _session.Patch<ItemInstance>(instance.Id.Value)
            .Set(x => x.PendingSpawn, instance.PendingSpawn)
            .Set(x => x.RootGameServerId, instance.RootGameServerId)
            .Set(x => x.UpdatedAt, instance.UpdatedAt);

    // Patch, never Store — see IItemInstanceRepository.WriteAppliedSnapshot's doc comment. The field
    // list is the snapshot's own surface and nothing else: naming a field is the only way this write
    // can touch it, so everything omitted here (the delivery/spawn-failure counters, RemovedByStaff,
    // and the immutable Origin/OriginRef/RegisteredAt trio) survives a concurrent writer intact, and
    // an UPDATE that matches no row — because another batch soft-deleted it in this batch's
    // load-to-save window — correctly writes nothing at all.
    public void WriteAppliedSnapshot(ItemInstance instance)
        => _session.Patch<ItemInstance>(instance.Id.Value)
            .Set(x => x.Revision, instance.Revision)
            .Set(x => x.ParentKind, instance.ParentKind)
            .Set(x => x.OwnerCharacterId, instance.OwnerCharacterId)
            .Set(x => x.ContainerInstanceId, instance.ContainerInstanceId)
            .Set(x => x.Slot, instance.Slot)
            .Set(x => x.Transform, instance.Transform)
            .Set(x => x.Durability, instance.Durability)
            .Set(x => x.Ammo, instance.Ammo)
            .Set(x => x.Attributes, instance.Attributes)
            .Set(x => x.PendingSpawn, instance.PendingSpawn)
            .Set(x => x.RootCharacterId, instance.RootCharacterId)
            .Set(x => x.RootGameServerId, instance.RootGameServerId)
            .Set(x => x.ExpiresAt, instance.ExpiresAt)
            .Set(x => x.LastSeenAt, instance.LastSeenAt)
            .Set(x => x.UpdatedAt, instance.UpdatedAt);

    // Patch, never Store — see IItemInstanceRepository.RecordDeliveryAttempt's doc comment for the
    // reproduced lost-update that resurrects PendingSpawn. Patch() emits a targeted jsonb_set on just
    // these two properties, so a concurrent ack's PendingSpawn/RootGameServerId writes survive it;
    // Store() would write the whole (stale) document back over them.
    public void RecordDeliveryAttempt(ItemInstance instance, DateTimeOffset now)
    {
        instance.RecordDeliveryAttempt(now);
        _session.Patch<ItemInstance>(instance.Id.Value)
            .Increment(x => x.DeliveryAttempts)
            .Set(x => x.UpdatedAt, now);
    }

    // Same reasoning as RecordDeliveryAttempt above, over the negative ack's three fields.
    public void RecordSpawnFailure(ItemInstance instance, SpawnFailureReason reason, DateTimeOffset now)
    {
        instance.RecordSpawnFailure(reason, now);
        _session.Patch<ItemInstance>(instance.Id.Value)
            .Set(x => x.LastSpawnFailureReason, (SpawnFailureReason?)reason)
            .Set(x => x.LastSpawnFailureAt, (DateTimeOffset?)now)
            .Increment(x => x.SpawnFailureCount)
            .Set(x => x.UpdatedAt, now);
    }

    // Patch, never Store — see IItemInstanceRepository.RewriteResolvedRoots' doc comment. A moved
    // container's descendants are rows this batch otherwise says nothing about, so replacing the whole
    // document from a possibly-stale loaded copy is exactly the lost-update that resurrects
    // PendingSpawn.
    public void RewriteResolvedRoots(
        ItemInstance instance,
        CharacterId? rootCharacterId,
        GameServerId? rootGameServerId,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        instance.RewriteResolvedRoots(rootCharacterId, rootGameServerId, expiresAt, now);
        _session.Patch<ItemInstance>(instance.Id.Value)
            .Set(x => x.RootCharacterId, rootCharacterId)
            .Set(x => x.RootGameServerId, rootGameServerId)
            .Set(x => x.ExpiresAt, expiresAt)
            .Set(x => x.UpdatedAt, now);
    }

    public void Eject(ItemInstance instance) => _session.Eject(instance);

    public void SoftDelete(ItemInstance instance)
    {
        // See ItemInstance.ClearPendingOnExplicitDelete's doc comment: an explicit delete clears
        // PendingSpawn as it deletes. Queuing a plain Store() alongside Delete() for the same id does
        // NOT work here — Marten's unit of work keeps only the last-registered document-storage
        // operation per id, so the Store() silently loses to the Delete() and the field change never
        // reaches Postgres (caught by
        // ItemInstanceRepositoryTests.ExplicitDelete_OfAPendingInstance_ClearsPendingAsItDeletes against
        // real Postgres). Patch() is tracked as a distinct operation kind, so it survives alongside
        // Delete() in the same SaveChangesAsync — mutate the in-memory instance too, purely so a
        // caller inspecting the object it just handed to this method sees the same value that was
        // written.
        var now = _timeProvider.GetUtcNow();
        instance.ClearPendingOnExplicitDelete(now);
        _session.Patch<ItemInstance>(instance.Id.Value).Set(x => x.PendingSpawn, false);
        _session.Delete<ItemInstance>(instance.Id);
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (FindChildSlotUniqueViolation(exception) is not null)
        {
            // Narrowed to this one named index (review round 2, item (ii)) — any other unique
            // violation (ExternalRef, or an unrelated schema-migration collision) propagates
            // unchanged, same as it did before this document had any unique constraint at all.
            throw new ChildSlotAlreadyMintedException();
        }
        // Fix round 1, item 7. Narrowed to ScopeCursor by DocType, the same discipline as the
        // child-slot narrowing above — any other JasperFx.ConcurrencyException (none exist today,
        // since ScopeCursor is the only document in this module with UseOptimisticConcurrency, but the
        // narrowing is what keeps this correct if that ever changes) propagates unchanged rather than
        // surfacing under a misleading type.
        catch (Exception exception) when (FindScopeCursorConflict(exception) is not null)
        {
            throw new ScopeCursorConflictException();
        }
    }

    private static PostgresException? FindChildSlotUniqueViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: UniqueViolation, ConstraintName: ChildSlotUniqueIndexName } postgres => postgres,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindChildSlotUniqueViolation).FirstOrDefault(x => x is not null),
        { InnerException: { } inner } => FindChildSlotUniqueViolation(inner),
        _ => null,
    };

    // ConcurrencyException.DocType is a string, not a System.Type — confirmed empirically against a
    // live optimistic-concurrency conflict. For a namespaced document it is the type's *full* name
    // (e.g. "ELifeRPG.World.Domain.Snapshots.ScopeCursor"), not its simple name — the first version of
    // this check compared against `nameof(ScopeCursor)` ("ScopeCursor" alone) and never matched,
    // silently letting the exception propagate unhandled; a live concurrent-race test against real
    // Postgres is what caught it. typeof(ScopeCursor).FullName keeps this tied to the actual type
    // rather than a hand-typed string literal that could silently drift from it.
    private static ConcurrencyException? FindScopeCursorConflict(Exception exception) => exception switch
    {
        ConcurrencyException { DocType: var docType } concurrency when docType == typeof(ScopeCursor).FullName => concurrency,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindScopeCursorConflict).FirstOrDefault(x => x is not null),
        { InnerException: { } inner } => FindScopeCursorConflict(inner),
        _ => null,
    };

    public async ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId,
        int quantity,
        CharacterId ownerCharacterId,
        ItemOrigin origin,
        OriginRef? originRef,
        CancellationToken cancellationToken)
    {
        var prefabClassName = await _catalogResolver.ResolvePrefabClassNameAsync(itemId, cancellationToken)
            ?? throw new ItemNotInCatalogException(itemId);

        return MintInstances(itemId, prefabClassName, quantity, ownerCharacterId, origin, originRef);
    }

    public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId,
        string? prefabClassName,
        int quantity,
        CharacterId ownerCharacterId,
        ItemOrigin origin,
        OriginRef? originRef,
        CancellationToken cancellationToken)
    {
        // Defense in depth only — see this overload's XML doc. A correct caller already resolved a
        // real prefab at its own precheck before any transaction opened; this never dispatches
        // anything itself, so there is no external call here to fail.
        if (string.IsNullOrEmpty(prefabClassName))
        {
            throw new ItemNotInCatalogException(itemId);
        }

        return ValueTask.FromResult(MintInstances(itemId, prefabClassName, quantity, ownerCharacterId, origin, originRef));
    }

    /// <summary>Shared by both <c>GrantAsync</c> overloads once a real <c>prefabClassName</c> is in hand — pure in-memory inserts, no I/O beyond <see cref="Store"/>.</summary>
    private IReadOnlyList<GrantedInstance> MintInstances(
        ItemId itemId, string prefabClassName, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef)
    {
        var now = _timeProvider.GetUtcNow();
        var granted = new List<GrantedInstance>(quantity);
        for (var i = 0; i < quantity; i++)
        {
            var instanceId = new ItemInstanceId(Guid.NewGuid());
            var instance = ItemInstance.Register(instanceId, itemId, ownerCharacterId, origin, originRef, now);
            _session.Store(instance);
            granted.Add(new GrantedInstance(instanceId, itemId, prefabClassName));
        }

        return granted;
    }
}
