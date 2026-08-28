using ELifeRPG.Shared.Infrastructure;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using ELifeRPG.World.Infrastructure.Common;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Linq.Expressions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class WorldInfrastructureExtensions
{
    /// <summary>
    /// Registers World's own Marten store on the `world` schema. Deliberately registers <b>zero</b>
    /// projections: `ItemInstance` is a plain Marten document, not an event-sourced aggregate.
    /// Under a snapshot/last-write-wins model the game sends a full set rather than a delta, and
    /// full-world persistence eventually requires pruning,
    /// which would permanently break projection rebuild. The corresponding guard test
    /// (`ItemInstance_IsNotRegisteredAsAProjection`) fails the build the day someone "fixes" this by
    /// adding one.
    /// </summary>
    public static IServiceCollection AddWorldInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<IWorldStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("WorldDatabase")!);
            options.DatabaseSchemaName = "world";
            options.Events.DatabaseSchemaName = "world";
            options.UseSystemTextJsonWithPrivateSetters();

            // RootCharacterId is the hot read — every character-inventory load filters on it. The
            // rest support the delivery loop (PendingSpawn), ground-item pruning (ExpiresAt), the
            // container tree (ContainerInstanceId) and per-server reads (RootGameServerId).
            // Attributes is deliberately never indexed — see ItemAttributes' doc comment.
            options.Schema.For<ItemInstance>()
                .Index(x => x.RootCharacterId)
                .Index(x => x.ContainerInstanceId)
                .Index(x => x.RootGameServerId)
                .Index(x => x.ExpiresAt)
                .Index(x => x.PendingSpawn)
                .SoftDeletedWithIndex()
                // A collided/reused external id (a phone device, say) must fail loudly rather than
                // silently double-link — hence unique — but most instances have no ExternalRef at
                // all, so the index is partial: only rows that actually carry one are constrained.
                .Index(x => x.ExternalRef!.Id, ix =>
                {
                    ix.IsUnique = true;
                    ix.Predicate = "(data -> 'ExternalRef' ->> 'Id') is not null";
                })
                // Task 5's anti-dupe fix for concurrent acks (review round 1, B-1): two acks for the
                // same parent+slot racing IItemInstanceRepository.FindChildrenAsync both see no
                // existing child and both try to mint one — a plain read-then-write with no DB
                // constraint cannot close that. Same partial-index shape as ExternalRef above: most
                // rows aren't container children at all, so the constraint only applies to the ones
                // that are. Named explicitly (rather than left to Marten's auto-generated name) so
                // MartenItemInstanceRepository.SaveChangesAsync can check
                // PostgresException.ConstraintName and translate only *this* violation into
                // ChildSlotAlreadyMintedException — review round 2, item (ii): any other unique
                // violation (ExternalRef, or a stray schema-migration collision) must propagate
                // unchanged rather than surface under a misleading type. The `!` on both lambda bodies
                // is a compile-time-only null-forgiving annotation for CS8603 (Marten builds a SQL
                // projection from the expression tree; it never evaluates it against a real, possibly
                // null value in-process) — same reasoning as the ExternalRef index's `x.ExternalRef!.Id`
                // above, just on the property itself rather than a nested one, since ContainerInstanceId
                // is the nullable half here.
                .Index(
                    new Expression<Func<ItemInstance, object>>[] { x => x.ContainerInstanceId!, x => x.Slot! },
                    ix =>
                    {
                        ix.Name = MartenItemInstanceRepository.ChildSlotUniqueIndexName;
                        ix.IsUnique = true;
                        // The predicate must match IItemInstanceRepository.FindChildrenAsync's filters
                        // exactly, because that read is what decides whether a slot is free and this
                        // index is what enforces it. Where they disagree, the ack path breaks in the
                        // worst possible way: the handler mints into a slot its own read said was
                        // empty, Postgres rejects the insert, and the reconciliation loop then re-reads
                        // the parent's children looking for the winner it lost to — and cannot see it,
                        // so `reconciledAny` stays false and the ack 500s instead of reconciling.
                        //
                        // Hence both exclusions (whole-branch review):
                        //   * soft-deleted rows — otherwise deleting a child makes its slot permanently
                        //     unusable, and Marten filters those out of the reconciling read by default.
                        //   * staff-removed rows — a sticky tombstone is not a live occupant. It stays a
                        //     real row on purpose (that is what makes it sticky: a later upsert of that
                        //     id must find it and be rejected rather than resurrect it), so without this
                        //     clause a staff removal would permanently burn the slot: the mod could
                        //     never re-declare a magazine there again, and every replayed ack would 500.
                        // Free to change now, before any row exists.
                        ix.Predicate =
                            "(data ->> 'ContainerInstanceId') is not null"
                            + " and mt_deleted = false"
                            + " and coalesce((data ->> 'RemovedByStaff')::boolean, false) = false";
                    });

            // Task 5: the staff promotion queue reads sort by Count descending and filter by both Count
            // and LastSeenAt (?minCount=&since=) — see MartenUnknownPrefabSightingRepository.FindForStaffAsync.
            // No SoftDeletedWithIndex(): a sighting is never deleted, only ever incremented in place.
            options.Schema.For<UnknownPrefabSighting>()
                .Index(x => x.Count)
                .Index(x => x.LastSeenAt);

            // Fix round 1, item 7: optimistic concurrency on ScopeCursor alone — no other document in
            // this module has it, and it must stay that way; every other write here relies on revision
            // LWW or a targeted patch for its own conflict story, not Marten's version column. Turns two
            // Full snapshot batches racing the same scope's cursor into one commit and one
            // ScopeCursorConflictException (caught in ApplySnapshotHandler and mapped to the endpoint's
            // one retryable outcome) instead of a silent last-write-wins on a value that must never
            // regress. See MartenScopeCursorRepository.AdvanceAsync for why every write also needs a
            // same-session Load first for this to mean anything.
            options.Schema.For<ScopeCursor>().UseOptimisticConcurrency(true);

            // Registered explicitly, not left to Marten's on-demand table creation, because the write
            // path added by the whole-branch review made this the first document here that is written
            // rarely and read on the hot path: without a registration the table is only created the
            // first time something stores one, so every read before the first-ever admin PATCH would
            // depend on AutoCreate being permissive at runtime. Deliberately no indexes and no
            // optimistic concurrency — it is a single row loaded by primary key, and a settings edit has
            // one writer at a time (see MartenWorldSettingsRepository.UpsertAsync).
            options.Schema.For<WorldSettings>();
        });

        // Injected rather than called statically so a grant's RegisteredAt/UpdatedAt is testable
        // without waiting on the real clock — same reasoning as PhoneInfrastructureExtensions.
        services.TryAddSingleton(TimeProvider.System);

        // Shared unit of work for the whole scope — see WorldSession for why this module needs one.
        services.TryAddScoped<IWorldSession, WorldSession>();
        services.TryAddScoped<IItemInstanceRepository, MartenItemInstanceRepository>();
        services.TryAddScoped<ITransactionParticipant<IItemInstanceRepository>, MartenItemInstanceParticipant>();
        services.TryAddScoped<IWorldSettingsRepository, MartenWorldSettingsRepository>();
        // Task 3: batch-level idempotency (AppliedBatch) and the Full-mode sequence gate (ScopeCursor).
        // Both are plain documents, not projections — see WorldStoreTests' guard test.
        services.TryAddScoped<IAppliedBatchRepository, MartenAppliedBatchRepository>();
        services.TryAddScoped<IScopeCursorRepository, MartenScopeCursorRepository>();
        // Task 4: the empty-payload guard's staff record. A plain document too — same guard test.
        services.TryAddScoped<ISuspiciousReconcileRepository, MartenSuspiciousReconcileRepository>();
        // Task 5: the unknown-prefab reporting/staff-promotion-queue document. A plain document too —
        // same guard test.
        services.TryAddScoped<IUnknownPrefabSightingRepository, MartenUnknownPrefabSightingRepository>();

        // The concrete resolver lives in World.Application (it's the one place in this module that
        // references Items.Application's ItemCatalogEntriesQuery) — see IItemCatalogResolver's doc
        // comment. Registering it here keeps this module's DI wiring in its usual single place.
        services.TryAddScoped<IItemCatalogResolver, ItemCatalogResolver>();

        return services;
    }
}
