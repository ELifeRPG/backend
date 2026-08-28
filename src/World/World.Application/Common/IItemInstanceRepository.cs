using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Application.Common;

/// <summary>
/// The read/write surface task 2 owns, plus task 3's <see cref="GrantAsync"/> for the cross-module
/// grant path (see the phase 1 plan's Controller rulings and <c>ITransactionParticipant{IItemInstanceRepository}</c>).
/// </summary>
public interface IItemInstanceRepository
{
    /// <summary>
    /// One instance by id, <b>excluding soft-deleted rows</b> — same visibility as every other read on
    /// this interface. Deliberately not Marten's <c>LoadAsync</c>: that is a direct id fetch which
    /// ignores the soft-delete filter entirely, so it happily returns a row an explicit delete already
    /// removed. That mismatch was live long enough for <c>SpawnFailedHandler</c> to accept a negative
    /// ack against an already-deleted instance (task 2 review, item 6); a soft-deleted row must look
    /// gone to every caller that isn't deliberately asking for tombstones.
    /// </summary>
    ValueTask<ItemInstance?> FindByIdAsync(ItemInstanceId id, CancellationToken cancellationToken);

    /// <summary>The hot read: every live instance whose denormalised root resolves to this character.</summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindByRootCharacterAsync(CharacterId rootCharacterId, CancellationToken cancellationToken);

    /// <summary>
    /// Task 4's <c>GET /api/inventory/characters/{characterId}/items</c>: what a character actually
    /// <b>holds</b> — the flat set of live instances rooted at <paramref name="rootCharacterId"/>,
    /// excluding soft-deleted (Marten's default query behaviour under <c>SoftDeletedWithIndex()</c>),
    /// staff-removed (<see cref="ItemInstance.RemovedByStaff"/>), expired (<c>ExpiresAt == null ||
    /// ExpiresAt &gt; now</c>) <b>and still-pending</b> (<see cref="ItemInstance.PendingSpawn"/>) rows.
    /// Deliberately unpaginated — see the design spec's "Carried inventory needs no pagination":
    /// Reforger's own volume system bounds what a character can carry.
    ///
    /// The <c>PendingSpawn</c> exclusion is what makes this the "holds" half of the holds-versus-owed
    /// split against <see cref="FindPendingByRootCharacterAsync"/>'s "owed" half: a row still awaiting
    /// its first spawn+ack has never actually reached the character, so it belongs only in the pending
    /// read. Without this exclusion every not-yet-acked row would surface on <i>both</i> reads, and a
    /// mod following each endpoint's own contract (spawn everything <c>/items</c> returns, then spawn
    /// everything <c>/pending</c> returns and ack it) would spawn it twice. An already-acked row still
    /// satisfies <c>!PendingSpawn</c> and so still reappears here after a server restart — that is all
    /// post-restart reconciliation needs; it says nothing about rows that were never delivered at all.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindCarriedByRootCharacterAsync(CharacterId rootCharacterId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Task 4's <c>GET /api/inventory/characters/{characterId}/pending?limit=</c>: what a character is
    /// still <b>owed</b> — instances not yet spawned (<see cref="ItemInstance.PendingSpawn"/>),
    /// oldest-first by <see cref="ItemInstance.RegisteredAt"/>, capped at <paramref name="limit"/> and
    /// excluding rows already at <paramref name="maxDeliveryAttempts"/>. Also excludes staff-removed and
    /// expired rows, same as <see cref="FindCarriedByRootCharacterAsync"/>. Bounded and paged — unlike
    /// the carried read — because pending rows are not bounded by Reforger's volume system: a player can
    /// buy from the portal repeatedly while offline, which is exactly the unbounded case the design
    /// spec's "The unbounded case is pending deliveries, not carried items" passage calls out.
    ///
    /// This is the complement of <see cref="FindCarriedByRootCharacterAsync"/>'s <c>PendingSpawn</c>
    /// clause, not an overlapping view of the same rows: a row is either held (surfaced by the other
    /// method) or owed (surfaced by this one), never both.
    ///
    /// Does not itself touch <see cref="ItemInstance.DeliveryAttempts"/> — the caller
    /// (<c>PendingDeliveriesHandler</c>) increments it on every row this returns, since a read that
    /// serves a pending row to the mod is what "an attempt" means.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindPendingByRootCharacterAsync(
        CharacterId rootCharacterId,
        int limit,
        int maxDeliveryAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// One batched load for however many ids a command needs — the whole answer to "no N round
    /// trips" for anything that must touch a set of instances at once (a container move, a batch
    /// upsert). See ARCHITECTURE.md §9e gotcha 4: compares the strongly-typed id, never <c>x.Id.Value</c>.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> LoadManyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Live children directly inside <b>any</b> of <paramref name="containerInstanceIds"/>, in one
    /// batched query — the descendant half of the snapshot write path's chain walk.
    ///
    /// Two things need it and neither can afford a query per container. Moving a container has to
    /// rewrite <see cref="ItemInstance.RootCharacterId"/>/<see cref="ItemInstance.RootGameServerId"/>/
    /// <see cref="ItemInstance.ExpiresAt"/> for everything nested inside it, and
    /// <see cref="ItemInstance.RootCharacterId"/> is the hot inventory read — leaving it stale surfaces
    /// a moved crate's contents in the <i>previous owner's</i> inventory. Deleting a container has to
    /// soft-delete its descendants, or a child keeps pointing at a row that no longer exists and is
    /// still returned by <see cref="FindCarriedByRootCharacterAsync"/>.
    ///
    /// Unlike <see cref="FindChildrenAsync"/> this does <b>not</b> filter
    /// <see cref="ItemInstance.RemovedByStaff"/>. That filter exists there to stop a replayed ack
    /// adopting a tombstoned child; here the opposite is wanted — a tombstoned row nested inside a
    /// container being deleted must go with it, or it stays reachable through a parent that is gone.
    ///
    /// The caller iterates this to <see cref="ItemInstance.MaxContainerDepth"/> to reach a whole
    /// subtree: at most six batched queries per request, constant in batch size, never one per
    /// instance.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindChildrenOfManyAsync(IReadOnlyList<ItemInstanceId> containerInstanceIds, CancellationToken cancellationToken);

    /// <summary>
    /// Live children directly inside <paramref name="containerInstanceId"/> — the ack path's idempotent
    /// child-minting (see <c>AcknowledgeSpawnsHandler</c>) uses this to find a child already minted for
    /// a given slot on a prior ack, so a replay returns that same id instead of minting a second one.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindChildrenAsync(ItemInstanceId containerInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// The staff queue backing <c>GET /api/inventory/undeliverable</c>: still-pending, not
    /// staff-removed rows that have been served <paramref name="maxDeliveryAttempts"/> times or more —
    /// see <see cref="ItemInstance.DeliveryAttempts"/> and the design spec's delivery-cap mechanism.
    /// "Undeliverable" is never its own stored flag; it is always this derived condition, checked at
    /// read time against whatever <c>WorldSettings.MaxDeliveryAttempts</c> currently is.
    /// </summary>
    ValueTask<IReadOnlyList<ItemInstance>> FindUndeliverableAsync(int maxDeliveryAttempts, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a whole-document write — an insert for a freshly minted row, an upsert for one that
    /// already exists.
    ///
    /// <b>Minting is the only unconditionally safe use.</b> On a row loaded from storage this writes
    /// back every field of whatever copy the caller happens to be holding, and <c>ItemInstance</c> has
    /// no optimistic concurrency to notice that the copy went stale — see
    /// <see cref="RecordDeliveryAttempt"/> for the reproduced lost update that costs, and
    /// <c>ItemInstanceRepositoryTests.Store_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_ResurrectsIt</c>
    /// for the worse one: Marten's document upsert clears the soft-delete marker, so this <i>undeletes</i>
    /// a row another writer removed in the meantime and brings the stale copy's
    /// <see cref="ItemInstance.PendingSpawn"/> back with it. A consumed item returns to the delivery
    /// queue and the player receives a second copy.
    ///
    /// No write path uses this on an already-stored row any more. The snapshot path patches through
    /// <see cref="WriteAppliedSnapshot"/> and <see cref="RewriteResolvedRoots"/>, and the ack path
    /// through <see cref="WriteAcknowledgedSpawn"/>. What is left is minting — <c>GrantAsync</c>'s new
    /// rows and the ack path's engine-spawned children — where the document does not yet exist and
    /// there is nothing stale to write back. A new caller reaching for this against a loaded row
    /// should reach for a patch instead.
    /// </summary>
    void Store(ItemInstance instance);

    /// <summary>
    /// Clears <see cref="ItemInstance.PendingSpawn"/> and stamps the resolved delivery server as a
    /// <b>targeted patch</b> — the ack path's counterpart to <see cref="WriteAppliedSnapshot"/>, over
    /// the three fields <see cref="ItemInstance.AcknowledgeSpawn"/> writes
    /// (<see cref="ItemInstance.PendingSpawn"/>, <see cref="ItemInstance.RootGameServerId"/>,
    /// <see cref="ItemInstance.UpdatedAt"/>).
    ///
    /// A patch for exactly the reason that method documents, reached through a different door. An ack
    /// arrives late by design — the Bridge is store-and-forward with retries — so the window between
    /// this handler's load and its save is wider here than anywhere else, and a
    /// <see cref="Store"/> landing in it undeletes a row a snapshot delete removed in the meantime.
    /// The concrete case: a granted item is consumed before its ack lands, the snapshot delete
    /// correctly removes it, and then the delayed ack puts it back into live inventory as an item the
    /// player already used. A patch against that row matches nothing, so the delete wins.
    ///
    /// Only ever called on the <c>PendingSpawn</c> true → false transition; a replayed ack for an
    /// already-cleared row never reaches here, same as the domain method it pairs with.
    /// </summary>
    void WriteAcknowledgedSpawn(ItemInstance instance);

    /// <summary>
    /// Writes one applied snapshot upsert as a <b>targeted patch</b> over exactly the fields a
    /// snapshot owns, read from <paramref name="instance"/> after
    /// <see cref="ItemInstance.ApplySnapshot"/> and <see cref="ItemInstance.RewriteResolvedRoots"/>
    /// have set them: the revision, the four parent-shaped fields, durability, ammo, attributes, the
    /// pending flag, the three resolved root fields, and the two timestamps.
    ///
    /// A patch rather than a <see cref="Store"/>, and the reasons compound:
    /// <list type="bullet">
    /// <item><b>It cannot resurrect a deleted row.</b> A patch is an <c>UPDATE</c>; against a row
    /// another writer soft-deleted between this batch's load and its save it simply matches nothing,
    /// which is the correct outcome — the delete wins. <see cref="Store"/> in that window undeletes
    /// the row <i>and</i> restores the stale <see cref="ItemInstance.PendingSpawn"/>, duplicating a
    /// consumed item.</item>
    /// <item><b>It cannot insert.</b> An <c>UPDATE</c> that matches nothing writes nothing, so the
    /// sole-minter rule is enforced by the storage operation itself rather than only by the handler
    /// check in front of it.</item>
    /// <item><b>It cannot clobber what it does not name.</b> The backend-owned fields — the delivery
    /// and spawn-failure counters, and <see cref="ItemInstance.RemovedByStaff"/> once staff tooling
    /// starts writing it — are absent from the field list and therefore survive a concurrent writer
    /// untouched.</item>
    /// </list>
    ///
    /// <see cref="ItemInstance.Origin"/>, <see cref="ItemInstance.OriginRef"/> and
    /// <see cref="ItemInstance.RegisteredAt"/> are deliberately absent too. They are already
    /// <c>private init</c>, so the domain makes them unassignable after construction; leaving them out
    /// of the patch means the persistence layer independently cannot overwrite them either — the same
    /// invariant enforced twice, by two mechanisms that fail differently.
    ///
    /// Mutates nothing itself: the caller has already applied the domain methods, and this only
    /// projects the resulting state into a patch.
    /// </summary>
    void WriteAppliedSnapshot(ItemInstance instance);

    /// <summary>
    /// Increments <see cref="ItemInstance.DeliveryAttempts"/> (and restamps
    /// <see cref="ItemInstance.UpdatedAt"/>) as an <b>atomic patch</b>, never a whole-document write.
    ///
    /// This is a correctness requirement, not an optimisation. <c>ItemInstance</c> has no optimistic
    /// concurrency, so a <see cref="Store"/> here writes back every field of whatever copy the caller
    /// happens to be holding. The Bridge is store-and-forward with Polly retries, so a duplicated
    /// <c>GET /pending</c> is expected: the retried handler loads a row while <c>PendingSpawn</c> is
    /// still true, the ack from the first read commits <c>PendingSpawn = false</c> plus a stamped
    /// <see cref="ItemInstance.RootGameServerId"/>, and the retried handler's whole-document save then
    /// puts both back — resurrecting the row into the pending queue, where it is offered again and
    /// spawned a second time. That is the exact duplication the delivery loop exists to prevent, and it
    /// was reproduced against real Postgres before this method existed (see
    /// <c>ItemInstanceRepositoryTests.RecordDeliveryAttempt_ForAnInstanceAckedConcurrently_DoesNotResurrectPendingSpawn</c>).
    /// A patch touches only the columns named here, so a concurrent ack survives it.
    ///
    /// Mutates <paramref name="instance"/> in memory too, purely so a caller inspecting the object it
    /// just handed over (e.g. to serialise into its own response) sees the same value that was written
    /// — same convention as <see cref="SoftDelete"/>.
    /// </summary>
    void RecordDeliveryAttempt(ItemInstance instance, DateTimeOffset now);

    /// <summary>
    /// Records a negative ack's three fields (<see cref="ItemInstance.LastSpawnFailureReason"/>,
    /// <see cref="ItemInstance.LastSpawnFailureAt"/>, <see cref="ItemInstance.SpawnFailureCount"/>) as
    /// an <b>atomic patch</b>, for exactly the reason <see cref="RecordDeliveryAttempt"/> documents:
    /// a whole-document write from a stale copy resurrects <see cref="ItemInstance.PendingSpawn"/>.
    /// </summary>
    void RecordSpawnFailure(ItemInstance instance, SpawnFailureReason reason, DateTimeOffset now);

    /// <summary>
    /// Writes the three derived root fields (<see cref="ItemInstance.RootCharacterId"/>,
    /// <see cref="ItemInstance.RootGameServerId"/>, <see cref="ItemInstance.ExpiresAt"/>) as an
    /// <b>atomic patch</b>, for exactly the reason <see cref="RecordDeliveryAttempt"/> documents.
    ///
    /// This is the snapshot write path's descendant cascade: when a container moves, every instance
    /// nested inside it needs its roots rewritten even though the batch said nothing about those rows
    /// themselves. A whole-document <see cref="Store"/> there would write back every other field of
    /// whatever copy the batch happened to load — including <see cref="ItemInstance.PendingSpawn"/>,
    /// which is the field the phase 1 review found resurrecting a paid item into the delivery queue.
    /// A descendant whose only real change is three derived fields must therefore patch exactly those
    /// three, never replace the document. A row the same batch <i>also</i> upserts goes
    /// through <see cref="WriteAppliedSnapshot"/> instead — the same kind of write over a wider field
    /// list, since there the batch owns the whole mod-facing state and not just the derived roots.
    ///
    /// Mutates <paramref name="instance"/> in memory too, same convention as
    /// <see cref="RecordDeliveryAttempt"/> and <see cref="SoftDelete"/>.
    /// </summary>
    void RewriteResolvedRoots(
        ItemInstance instance,
        CharacterId? rootCharacterId,
        GameServerId? rootGameServerId,
        DateTimeOffset? expiresAt,
        DateTimeOffset now);

    /// <summary>
    /// Removes <paramref name="instance"/> from this session's pending changes entirely — used only by
    /// <c>AcknowledgeSpawnsHandler</c>'s reconciliation after a <see cref="ChildSlotAlreadyMintedException"/>:
    /// a speculatively-minted child that lost the race must never be retried on the next
    /// <see cref="SaveChangesAsync"/>, since by then the winning row already exists at that slot.
    /// </summary>
    void Eject(ItemInstance instance);

    /// <summary>
    /// An explicit delete — the caller (e.g. a future snapshot delete, or staff tooling) is asserting
    /// this row is genuinely gone, not merely absent from some reconcile payload. Clears
    /// <see cref="ItemInstance.PendingSpawn"/> as it deletes (see
    /// <see cref="ItemInstance.ClearPendingOnExplicitDelete"/>'s doc comment) — leaving the flag set on
    /// a soft-deleted row would still make sense to no future reader, but this is the one write path
    /// where getting that wrong actually re-spawns a legitimately-consumed item.
    /// </summary>
    void SoftDelete(ItemInstance instance);

    /// <exception cref="ChildSlotAlreadyMintedException">
    /// A pending child insert lost a race against a concurrent ack for the same
    /// (<see cref="ItemInstance.ContainerInstanceId"/>, <see cref="ItemInstance.Slot"/>) pair — see
    /// the partial unique index in <c>World.Infrastructure/ServiceCollectionExtensions.cs</c>.
    /// <c>AcknowledgeSpawnsHandler</c> catches this and reconciles; no other caller in phase 1 can
    /// trigger it.
    /// </exception>
    ValueTask SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Mints <paramref name="quantity"/> brand-new, character-owned instances of
    /// <paramref name="itemId"/> in one call — one row per discrete entity, since Reforger has no
    /// item stacking (ten bandages mint ten rows, never a stack of ten). Every minted row is
    /// <c>PendingSpawn = true</c>, <c>Revision = 0</c>, <c>RootGameServerId = null</c> (the delivery
    /// server resolves at spawn time from <c>Character.CurrentServerId</c>) — see
    /// <see cref="ItemInstance.Register"/>.
    ///
    /// Deliberately takes <b>no row lock</b>: a grant never stacks onto an existing row, so there is
    /// nothing to contend with. This is load-bearing for <c>PurchaseListingHandler</c> (task 6), which
    /// already holds two bank-account locks in a carefully sorted order — a third lockable resource
    /// here would reintroduce exactly the deadlock that ordering exists to avoid.
    ///
    /// Only queues the writes via <see cref="Store"/>; the caller still calls
    /// <see cref="SaveChangesAsync"/> before committing, same as every other write through this
    /// repository (see <c>WorldSession</c>). The cap on <paramref name="quantity"/>
    /// (<c>WorldSettings.MaxInstancesPerGrant</c>) is checked by the caller before opening its
    /// transaction, not here — this method always mints exactly what it's asked for.
    ///
    /// Resolves <see cref="GrantedInstance.PrefabClassName"/> itself, through the batched
    /// <c>ItemCatalogEntriesQuery</c> contract (Items.Application) dispatched via <c>IMediator</c> —
    /// convenient for a caller with no cross-module transaction open (e.g. <c>GrantItemsHandler</c>),
    /// but that dispatch opens Items' own scoped session, a second pooled connection distinct from
    /// this repository's own. A caller already holding a shared <c>ICrossModuleTransaction</c> (task
    /// 6's <c>PurchaseListingHandler</c>, task 7's gathering orchestrator) must use the
    /// <see cref="GrantAsync(ItemId,string,int,CharacterId,ItemOrigin,OriginRef?,CancellationToken)"/>
    /// overload below instead — resolving the prefab at its own precheck, before any lock is taken —
    /// so the in-transaction path never opens a second connection while it holds row locks on money.
    /// </summary>
    /// <exception cref="ItemNotInCatalogException">
    /// <paramref name="itemId"/> has no catalog entry to resolve a <c>PrefabClassName</c> from.
    /// <c>GrantItemsHandler</c> catches this and maps it to <c>GrantItemsResult.ItemNotInCatalog</c>.
    /// </exception>
    ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId,
        int quantity,
        CharacterId ownerCharacterId,
        ItemOrigin origin,
        OriginRef? originRef,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same mint as the overload above, but takes an already-resolved
    /// <paramref name="prefabClassName"/> instead of resolving one itself — for a caller that already
    /// holds a shared <c>ICrossModuleTransaction</c> (task 6's <c>PurchaseListingHandler</c>, task 7's
    /// gathering orchestrator). Those callers resolve the catalog entry at their own precheck, before
    /// opening the transaction and before taking any row lock, specifically so this method — called
    /// while those locks are held — does pure in-memory inserts with no external dispatch: opening a
    /// second pooled connection into Items' own scoped session while holding the listing lock and both
    /// bank-account locks would be a resource-starvation risk under pool saturation, even though it
    /// adds no lock-<i>ordering</i> edge and so isn't itself a deadlock (see the review that added this
    /// overload).
    ///
    /// Still throws <see cref="ItemNotInCatalogException"/> if <paramref name="prefabClassName"/> is
    /// null or empty — defense in depth for a caller that reaches this method without having actually
    /// resolved one, not the normal path (a correct caller's precheck already turned a missing catalog
    /// entry into its own pre-payment rejection before any transaction opened).
    /// </summary>
    /// <exception cref="ItemNotInCatalogException"><paramref name="prefabClassName"/> is null or empty.</exception>
    ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId,
        string? prefabClassName,
        int quantity,
        CharacterId ownerCharacterId,
        ItemOrigin origin,
        OriginRef? originRef,
        CancellationToken cancellationToken);
}
