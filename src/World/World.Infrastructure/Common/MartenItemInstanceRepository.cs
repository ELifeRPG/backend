using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
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
    /// Used only by <c>MartenItemInstanceRepositoryFactory</c> for cross-module atomic writes — the
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
        => await _session.LoadAsync<ItemInstance>(id, cancellationToken);

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
    }

    private static PostgresException? FindChildSlotUniqueViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: UniqueViolation, ConstraintName: ChildSlotUniqueIndexName } postgres => postgres,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindChildSlotUniqueViolation).FirstOrDefault(x => x is not null),
        { InnerException: { } inner } => FindChildSlotUniqueViolation(inner),
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
