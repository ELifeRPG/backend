using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>One declared child of an acked instance — see <see cref="ItemInstance.RegisterChild"/>.</summary>
public sealed record AckChildRequest(ItemId ItemId, string Slot);

/// <summary>One entry of a batched ack — see <see cref="AcknowledgeSpawnsCommand"/>.</summary>
public sealed record InstanceAckRequest(ItemInstanceId InstanceId, IReadOnlyList<AckChildRequest> Children);

/// <summary>
/// The outcome of minting (or re-finding) one declared child — see
/// <see cref="AcknowledgeSpawnsHandler"/>'s idempotent child-minting.
/// </summary>
public union AckChildOutcome(AckChildOutcome.Minted, AckChildOutcome.ItemNotInCatalog, AckChildOutcome.SlotItemMismatch)
{
    public record Minted(ItemInstanceId InstanceId);

    /// <summary>
    /// Maps the same uncatalogued-item condition <c>GrantAsync</c> raises as
    /// <c>ItemNotInCatalogException</c> — a child carries its own <c>ItemId</c> (see the phase 1 task
    /// brief) and so can name one with no catalog entry. Reported per-child rather than failing the
    /// whole ack, consistent with every other per-instance outcome in this batch.
    /// </summary>
    public record ItemNotInCatalog;

    /// <summary>
    /// This slot already has a minted child — but for a *different* <see cref="ItemId"/> than this ack
    /// declares. Added in review round 1 (B-2): the original cut matched on slot alone and silently
    /// returned the existing child's id while echoing the caller's own (wrong) itemId back, which tells
    /// the mod to adopt an instance whose persisted item does not match what it thinks it is. No
    /// duplicate row is created — this is an adoption-integrity hole, not a dupe — so it is reported
    /// rather than silently resolved either way; the mod (or a human) has to work out which of the two
    /// itemIds is actually right for that slot.
    /// </summary>
    public record SlotItemMismatch(ItemId ExistingItemId);
}

/// <summary>One child as declared on the request, paired with what happened when it was resolved.</summary>
public sealed record AckedChild(ItemId ItemId, string Slot, AckChildOutcome Outcome);

/// <summary>
/// The result for one acked instance. <see cref="Cleared"/> and <see cref="AlreadyCleared"/> both
/// carry the resolved children — idempotency means a replay lands in <see cref="AlreadyCleared"/> but
/// must still report the same child ids as the first, successful ack.
/// </summary>
public union AckOutcome(AckOutcome.Cleared, AckOutcome.AlreadyCleared, AckOutcome.NotFound, AckOutcome.WrongServer, AckOutcome.RemovedByStaff)
{
    public record Cleared(IReadOnlyList<AckedChild> Children);

    public record AlreadyCleared(IReadOnlyList<AckedChild> Children);

    /// <summary>
    /// The id was never granted by the backend — adoption is mandatory, there is no remap (see the
    /// phase 1 task brief). This is the loudest anti-dupe signal this design has: by construction,
    /// either the mod invented an id itself or something is badly wrong.
    /// </summary>
    public record NotFound;

    /// <summary>
    /// The id was granted, but to a character not currently on the calling gameserver — the server
    /// guard (<c>Character.CurrentServerId</c> via <c>CharactersOnServerQuery</c>). Split from
    /// <see cref="NotFound"/> in review round 1 (B-4): the design gives these two an operationally
    /// opposite meaning elsewhere (an unknown id is a mod bug or worse; a wrong-server ack is just a
    /// player hopping servers), so folding them made the loud signal invisible. Note that
    /// <c>CharactersOnServerQuery</c>'s own doc comment says to treat "not on this server" and "no such
    /// character" identically only for *the decision* to reject the write — not to report them
    /// identically; both still land here as <see cref="WrongServer"/> because from an already-known
    /// instance's perspective they're the same failure (its character isn't reachable from here), which
    /// is a different question from whether the *instance id itself* was ever granted.
    /// </summary>
    public record WrongServer;

    /// <summary>The sticky tombstone: a backend removal marks an instance RemovedByStaff, and it is never un-tombstoned by an ack.</summary>
    public record RemovedByStaff;
}

/// <summary>One acked instance id paired with its outcome.</summary>
public sealed record InstanceAckOutcome(ItemInstanceId InstanceId, AckOutcome Outcome);

/// <summary>
/// Batch-level result. Per-instance problems are reported per instance (see <see cref="AckOutcome"/>)
/// and never fail the batch; the one thing that <i>does</i> is an over-sized request, which the design
/// spec makes a first-class <c>batch_too_large</c> rejection (400, not retryable — chunk and resend).
/// Enforced as a <b>count</b> against <c>WorldSettings.MaxAcksPerBatch</c>/<c>MaxChildrenPerAck</c>,
/// never as a body size, and both caps are published on <c>GET /api/inventory/limits</c> so the Bridge
/// chunks correctly rather than discovering them as rejections. (Whole-branch review, I5.)
/// </summary>
public union AcknowledgeSpawnsResult(AcknowledgeSpawnsResult.Acknowledged, AcknowledgeSpawnsResult.BatchTooLarge)
{
    public record Acknowledged(IReadOnlyList<InstanceAckOutcome> Outcomes);

    /// <summary><paramref name="Field"/> names which cap was exceeded — <c>acks</c> or <c>children</c> — so the Bridge knows which axis to chunk on.</summary>
    public record BatchTooLarge(string Field, int Requested, int Max);
}

/// <summary>
/// Backs <c>POST /api/inventory/acks</c> — the mod's batched confirmation that it spawned one or more
/// backend-granted instances, closing the <see cref="ItemInstance.PendingSpawn"/> loop. See the
/// design spec's "Delivering a granted instance" and "The correctness core" (mechanism 2): adoption is
/// mandatory, there is no remap, and this is the only write path in phase 1 that ever clears
/// <see cref="ItemInstance.PendingSpawn"/> from true to false.
/// </summary>
public sealed record AcknowledgeSpawnsCommand(GameServerId GameServerId, IReadOnlyList<InstanceAckRequest> Acks)
    : IRequest<AcknowledgeSpawnsResult>;

/// <summary>
/// Server-guarded on <c>Character.CurrentServerId</c> via the batched <c>CharactersOnServerQuery</c>
/// contract (Characters.Application) — deliberately not <c>SessionActive</c>, which
/// <c>Character.cs</c> documents as unreliable after an ungraceful gameserver crash. Batched once for
/// the whole request rather than per acked instance, same reasoning as that query's own doc comment:
/// this path must not put N cross-module round trips in front of every ack batch.
///
/// Child minting is idempotent on replay, keyed by slot (see <see cref="ItemInstance.RegisterChild"/>'s
/// doc comment): a per-parent cache is populated at most once per call to <see cref="Handle"/> (one DB
/// round trip via <c>IItemInstanceRepository.FindChildrenAsync</c>), then consulted — and updated in
/// place with anything freshly minted — for every children entry that names that same parent, including
/// a second entry for the same instance id within one batch. That covers a *sequential* replay (a
/// second, separate ack request after the first already committed) and duplicate entries within one
/// batch, but not two acks racing *concurrently* against the same parent+slot — see
/// <see cref="SaveReconcilingChildSlotConflictsAsync"/> for that (review round 1, B-1).
/// </summary>
public sealed class AcknowledgeSpawnsHandler(
    IItemInstanceRepository repository,
    IItemCatalogResolver catalogResolver,
    IWorldSettingsRepository settingsRepository,
    IMediator mediator,
    TimeProvider timeProvider)
    : IRequestHandler<AcknowledgeSpawnsCommand, AcknowledgeSpawnsResult>
{
    /// <summary>
    /// A single genuine concurrent conflict resolves in one retry (the loser reconciles against the
    /// winner and the next save has nothing left to fight over). This just bounds the pathological
    /// case — persistent contention on the very same slot across every attempt — rather than looping
    /// forever.
    /// </summary>
    private const int MaxChildSlotConflictRetries = 5;

    private enum AckKind
    {
        NotFound,
        WrongServer,
        RemovedByStaff,
        Cleared,
        AlreadyCleared,
    }

    /// <summary>
    /// Mutable working state for one child across the whole <see cref="Handle"/> call, so a
    /// lost-the-race child (see <see cref="SaveReconcilingChildSlotConflictsAsync"/>) can be swapped
    /// in place for the winner's row without rebuilding the immutable result records early.
    /// </summary>
    private sealed class WorkingChild
    {
        public required ItemId ItemId;
        public required string Slot;

        /// <summary>Null only when <see cref="ItemNotInCatalog"/> is true or <see cref="MismatchedExistingItemId"/> is set.</summary>
        public ItemInstance? Instance;

        public bool IsNewlyMinted;
        public bool ItemNotInCatalog;
        public ItemId? MismatchedExistingItemId;
    }

    private sealed class WorkingAck
    {
        public required ItemInstanceId InstanceId;
        public AckKind Kind;
        public List<WorkingChild> Children = [];
    }

    public async ValueTask<AcknowledgeSpawnsResult> Handle(AcknowledgeSpawnsCommand request, CancellationToken cancellationToken)
    {
        if (request.Acks.Count == 0)
        {
            return new AcknowledgeSpawnsResult.Acknowledged([]);
        }

        // Count caps first, before a single row is read — an over-sized batch is rejected whole, and
        // rejecting it after the loads would have made the cap useless as the lock-duration bound it
        // also is. Grants are capped by MaxInstancesPerGrant and pending pages by MaxPendingPageSize;
        // this closes the one remaining uncapped write surface. (Whole-branch review, I5.)
        var settings = await settingsRepository.GetAsync(cancellationToken);
        if (request.Acks.Count > settings.MaxAcksPerBatch)
        {
            return new AcknowledgeSpawnsResult.BatchTooLarge("acks", request.Acks.Count, settings.MaxAcksPerBatch);
        }

        foreach (var ack in request.Acks)
        {
            if (ack.Children.Count > settings.MaxChildrenPerAck)
            {
                return new AcknowledgeSpawnsResult.BatchTooLarge("children", ack.Children.Count, settings.MaxChildrenPerAck);
            }
        }

        var now = timeProvider.GetUtcNow();
        var ids = request.Acks.Select(x => x.InstanceId).Distinct().ToList();
        var loaded = await repository.LoadManyAsync(ids, cancellationToken);
        var byId = loaded.ToDictionary(x => x.Id);

        // One batched guard call for the whole request — see this class's own doc comment.
        var candidateCharacterIds = loaded
            .Where(x => !x.RemovedByStaff)
            .Select(x => x.RootCharacterId)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        var onThisServer = candidateCharacterIds.Count == 0
            ? new HashSet<CharacterId>()
            : await mediator.Send(new CharactersOnServerQuery(request.GameServerId, candidateCharacterIds), cancellationToken);

        // One batched catalog check for every child declared anywhere in this request, resolved before
        // the per-ack loop. Previously the resolver was called inside that loop, once per child, and
        // ItemCatalogResolver dispatches the *batched* ItemCatalogEntriesQuery with a single id and no
        // memoisation — so N children of the same item meant N cross-module round trips (each opening
        // Items' own scoped session) while this module's session stayed open. The design's write-path
        // mechanics call for "one batched catalog check"; this is it. (Whole-branch review, I5.)
        var declaredChildItemIds = request.Acks.SelectMany(x => x.Children).Select(x => x.ItemId).Distinct().ToList();
        var childPrefabsByItemId = await catalogResolver.ResolvePrefabClassNamesAsync(declaredChildItemIds, cancellationToken);

        var childrenCacheByParent = new Dictionary<ItemInstanceId, Dictionary<string, ItemInstance>>();
        var working = new List<WorkingAck>(request.Acks.Count);

        foreach (var ack in request.Acks)
        {
            var entry = new WorkingAck { InstanceId = ack.InstanceId, Kind = AckKind.NotFound };
            working.Add(entry);

            if (!byId.TryGetValue(ack.InstanceId, out var instance))
            {
                continue;
            }

            if (instance.RemovedByStaff)
            {
                entry.Kind = AckKind.RemovedByStaff;
                continue;
            }

            if (instance.RootCharacterId is not { } rootCharacterId || !onThisServer.Contains(rootCharacterId))
            {
                entry.Kind = AckKind.WrongServer;
                continue;
            }

            var wasPending = instance.PendingSpawn;
            if (wasPending)
            {
                instance.AcknowledgeSpawn(request.GameServerId, now);

                // A targeted patch, not a Store() of this loaded copy. An ack is the write with the
                // widest load-to-save window in the module — the Bridge is store-and-forward with
                // retries, so it can arrive long after the batch that produced it — and Marten's
                // document upsert clears the soft-delete marker, so a Store() here undeletes a row a
                // snapshot delete removed in the meantime. That is the "granted item consumed before
                // its ack landed" case putting an already-used item back into live inventory. See
                // IItemInstanceRepository.WriteAcknowledgedSpawn.
                repository.WriteAcknowledgedSpawn(instance);
            }

            entry.Kind = wasPending ? AckKind.Cleared : AckKind.AlreadyCleared;
            entry.Children = await ResolveChildrenAsync(instance, ack.Children, childrenCacheByParent, childPrefabsByItemId, now, cancellationToken);
        }

        await DiscardChildrenOfVanishedParentsAsync(working, cancellationToken);
        await SaveReconcilingChildSlotConflictsAsync(working, cancellationToken);

        return new AcknowledgeSpawnsResult.Acknowledged(working.Select(ToOutcome).ToList());
    }

    /// <summary>
    /// Drops speculatively-minted children whose parent has been soft-deleted since this handler
    /// loaded it, and reports that ack as <see cref="AckOutcome.NotFound"/>.
    ///
    /// The case is the ack path's own version of the race
    /// <c>IItemInstanceRepository.WriteAcknowledgedSpawn</c> documents: a granted item is consumed
    /// before its ack lands, a snapshot delete correctly removes it, and the delayed ack arrives
    /// afterwards. Patching the parent already handles itself — the <c>UPDATE</c> matches nothing, so
    /// the row stays deleted — but a child insert is not an update. Minted children would land as
    /// live, non-pending rows carrying the deleted parent's <see cref="ItemInstance.RootCharacterId"/>
    /// and a <see cref="ItemInstance.ContainerInstanceId"/> pointing at a row that no longer exists.
    /// They would answer the carried-inventory read, which is exactly the orphaned state the snapshot
    /// path's delete cascade exists to prevent, arriving through a door that cascade cannot see.
    ///
    /// Costs one batched read, and only for a request that actually declares children — the common
    /// ack carries none and pays nothing. Reuses <c>Eject</c>, the same mechanism the child-slot
    /// reconciliation below uses to abandon an insert that must not be retried.
    ///
    /// <b>Narrows the window rather than eliminating it</b>, and that is worth stating plainly: a
    /// delete committing between this check and the <c>SaveChangesAsync</c> below still produces the
    /// orphan. Closing it outright needs the delete and the ack to serialise on the same row lock,
    /// which neither path takes today and which is a larger design decision than this fix. What is
    /// removed here is the whole span from the handler's initial load — where the delete has real time
    /// to land, since an ack is store-and-forward and arrives late by design — down to a window of
    /// microseconds.
    /// </summary>
    private async ValueTask DiscardChildrenOfVanishedParentsAsync(List<WorkingAck> working, CancellationToken cancellationToken)
    {
        var parentIds = working
            .Where(x => x.Children.Any(child => child.IsNewlyMinted))
            .Select(x => x.InstanceId)
            .Distinct()
            .ToList();

        if (parentIds.Count == 0)
        {
            return;
        }

        // Soft-deleted rows are absent from this read (IItemInstanceRepository.LoadManyAsync queries
        // rather than loads by id), which is precisely the signal wanted here.
        var stillLive = (await repository.LoadManyAsync(parentIds, cancellationToken)).Select(x => x.Id).ToHashSet();

        foreach (var ack in working)
        {
            if (stillLive.Contains(ack.InstanceId) || !ack.Children.Any(child => child.IsNewlyMinted))
            {
                continue;
            }

            foreach (var child in ack.Children)
            {
                if (child is { IsNewlyMinted: true, Instance: not null })
                {
                    repository.Eject(child.Instance);
                }
            }

            // NotFound rather than Cleared: by the time this batch committed, the instance did not
            // exist. Saying otherwise would tell the mod it had successfully adopted a row that is
            // gone.
            ack.Kind = AckKind.NotFound;
            ack.Children = [];
        }
    }

    private async ValueTask<List<WorkingChild>> ResolveChildrenAsync(
        ItemInstance parent,
        IReadOnlyList<AckChildRequest> requestedChildren,
        Dictionary<ItemInstanceId, Dictionary<string, ItemInstance>> childrenCacheByParent,
        IReadOnlyDictionary<ItemId, string> childPrefabsByItemId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var results = new List<WorkingChild>(requestedChildren.Count);
        if (requestedChildren.Count == 0)
        {
            return results;
        }

        if (!childrenCacheByParent.TryGetValue(parent.Id, out var bySlot))
        {
            var existing = await repository.FindChildrenAsync(parent.Id, cancellationToken);
            bySlot = existing.Where(x => x.Slot is not null).ToDictionary(x => x.Slot!);
            childrenCacheByParent[parent.Id] = bySlot;
        }

        foreach (var child in requestedChildren)
        {
            if (bySlot.TryGetValue(child.Slot, out var existingChild))
            {
                // B-2: a slot already minted for a different itemId must never be silently adopted
                // under the caller's (wrong) itemId.
                results.Add(existingChild.ItemId == child.ItemId
                    ? new WorkingChild { ItemId = child.ItemId, Slot = child.Slot, Instance = existingChild }
                    : new WorkingChild { ItemId = child.ItemId, Slot = child.Slot, MismatchedExistingItemId = existingChild.ItemId });
                continue;
            }

            // Already resolved in the one batched lookup at the top of Handle — absent means the child
            // named an itemId with no catalog entry, reported per-child rather than failing the ack.
            if (!childPrefabsByItemId.ContainsKey(child.ItemId))
            {
                results.Add(new WorkingChild { ItemId = child.ItemId, Slot = child.Slot, ItemNotInCatalog = true });
                continue;
            }

            var childInstance = ItemInstance.RegisterChild(new ItemInstanceId(Guid.NewGuid()), child.ItemId, parent, child.Slot, now);
            repository.Store(childInstance);
            bySlot[child.Slot] = childInstance;
            results.Add(new WorkingChild { ItemId = child.ItemId, Slot = child.Slot, Instance = childInstance, IsNewlyMinted = true });
        }

        return results;
    }

    /// <summary>
    /// Guards against B-1 (review round 1): two concurrent acks for the same parent+slot both seeing
    /// no existing child (via <see cref="ResolveChildrenAsync"/>) and both attempting to mint one. The
    /// partial unique index on <c>(ContainerInstanceId, Slot)</c> — see
    /// <c>World.Infrastructure/ServiceCollectionExtensions.cs</c> — makes the loser's insert fail
    /// rather than silently succeed; <c>MartenItemInstanceRepository.SaveChangesAsync</c> translates
    /// that into <see cref="ChildSlotAlreadyMintedException"/>.
    ///
    /// On catch, every speculatively-minted (not-yet-committed) child from this call is re-verified
    /// against a fresh read of its parent's children. Anything that lost the race is ejected from the
    /// session (so the next save doesn't retry the now-doomed insert) and swapped for the winner's row
    /// — or, if the winner minted a *different* itemId into that slot, reported as
    /// <see cref="AckChildOutcome.SlotItemMismatch"/> instead of silently adopting the wrong item (same
    /// rule as B-2). The save is then retried; this does not require parsing which specific key
    /// violated, since re-checking every speculative insert against fresh state finds every conflict
    /// regardless of which one Postgres happened to report first.
    /// </summary>
    private async ValueTask SaveReconcilingChildSlotConflictsAsync(List<WorkingAck> working, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await repository.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ChildSlotAlreadyMintedException) when (attempt < MaxChildSlotConflictRetries)
            {
                var newlyMinted = working
                    .SelectMany(x => x.Children)
                    .Where(x => x.IsNewlyMinted && x.Instance is not null)
                    .ToList();

                var reconciledAny = false;

                foreach (var group in newlyMinted.GroupBy(x => x.Instance!.ContainerInstanceId!.Value))
                {
                    var fresh = await repository.FindChildrenAsync(group.Key, cancellationToken);
                    var freshBySlot = fresh.Where(x => x.Slot is not null).ToDictionary(x => x.Slot!);

                    foreach (var pending in group)
                    {
                        if (!freshBySlot.TryGetValue(pending.Slot, out var winner) || winner.Id == pending.Instance!.Id)
                        {
                            continue; // still ours (or nothing landed yet) — not the conflict
                        }

                        repository.Eject(pending.Instance);
                        pending.IsNewlyMinted = false;

                        if (winner.ItemId == pending.ItemId)
                        {
                            pending.Instance = winner;
                        }
                        else
                        {
                            pending.Instance = null;
                            pending.MismatchedExistingItemId = winner.ItemId;
                        }

                        reconciledAny = true;
                    }
                }

                if (!reconciledAny)
                {
                    // A unique-constraint violation we couldn't attribute to a known speculative
                    // mint — not a case this reconciliation loop knows how to fix. Let it propagate
                    // rather than silently retry against an unchanged write set.
                    throw;
                }
            }
        }
    }

    private static InstanceAckOutcome ToOutcome(WorkingAck ack) => new(
        ack.InstanceId,
        ack.Kind switch
        {
            AckKind.NotFound => new AckOutcome.NotFound(),
            AckKind.WrongServer => new AckOutcome.WrongServer(),
            AckKind.RemovedByStaff => new AckOutcome.RemovedByStaff(),
            AckKind.Cleared => new AckOutcome.Cleared(ack.Children.Select(ToChildOutcome).ToList()),
            AckKind.AlreadyCleared => new AckOutcome.AlreadyCleared(ack.Children.Select(ToChildOutcome).ToList()),
            _ => throw new InvalidOperationException($"Unreachable AckKind '{ack.Kind}'."),
        });

    private static AckedChild ToChildOutcome(WorkingChild child) => new(
        child.ItemId,
        child.Slot,
        child.MismatchedExistingItemId is { } mismatch
            ? new AckChildOutcome.SlotItemMismatch(mismatch)
            : child.ItemNotInCatalog
                ? new AckChildOutcome.ItemNotInCatalog()
                : new AckChildOutcome.Minted(child.Instance!.Id));
}
