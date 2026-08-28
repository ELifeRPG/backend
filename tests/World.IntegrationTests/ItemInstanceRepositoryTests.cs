using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Marten.Linq.SoftDeletes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers
/// <c>MartenItemInstanceRepository</c> against the running Postgres — the DI-registered surface task
/// 2 owns (FindByIdAsync, FindByRootCharacterAsync, LoadManyAsync, Store, SoftDelete, SaveChangesAsync).
/// </summary>
public sealed class ItemInstanceRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static ItemInstance Register(CharacterId ownerCharacterId)
        => ItemInstance.Register(
            new ItemInstanceId(Guid.NewGuid()),
            new ItemId(Guid.NewGuid()),
            ownerCharacterId,
            ItemOrigin.ShopPurchase,
            new OriginRef("Shops", Guid.NewGuid().ToString()),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Task 2 review, item 6: <c>FindByIdAsync</c> used to go through Marten's <c>LoadAsync</c>, a
    /// direct id fetch that ignores the soft-delete filter every other read on this repository
    /// applies — so a deleted row still came back, and <c>SpawnFailedHandler</c> would happily record
    /// a negative ack against it.
    /// </summary>
    [Fact]
    public async Task FindById_ForASoftDeletedInstance_ReturnsNull()
    {
        var instance = Register(new CharacterId(Guid.NewGuid()));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            Assert.Null(await repository.FindByIdAsync(instance.Id, CancellationToken.None));
        }

        // The row is still there, just soft-deleted — this is a visibility fix, not a hard delete.
        await using var readScope = _provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();
        var tombstoned = await session.Query<ItemInstance>()
            .Where(x => x.Id == instance.Id && x.MaybeDeleted())
            .SingleOrDefaultAsync();
        Assert.NotNull(tombstoned);
    }

    /// <summary>
    /// <b>Documents Marten's engine behaviour, not this codebase's.</b> Nothing in the snapshot write
    /// path does what this test does any more — that is the point of it. It is the evidence for why
    /// <see cref="IItemInstanceRepository.WriteAppliedSnapshot"/> exists and is a patch, and the next
    /// person who proposes "simplifying" that back to a <c>Store()</c> has to get past this first.
    /// A Marten bump that changes the behaviour will fail here loudly rather than quietly altering
    /// what the fix was for.
    ///
    /// <b>Observed, empirically, before any of it was designed around:</b> <c>Store()</c> of a copy
    /// loaded <i>before</i> another writer soft-deleted the row <b>resurrects it</b>. It is visible to
    /// ordinary (non-<c>MaybeDeleted</c>) queries again, carrying the stale copy's field values with
    /// <see cref="ItemInstance.PendingSpawn"/> among them. Marten's document upsert writes
    /// <c>mt_deleted = false</c> rather than leaving the soft-delete columns alone, so a deleted id is
    /// not durably gone as far as a later whole-document write is concerned.
    ///
    /// What that cost, when the snapshot path still wrote applied upserts with <c>Store()</c>: a
    /// delete committing inside a batch's load-to-save window brought a consumed item back, and
    /// brought it back <i>pending</i>, so the delivery loop re-served it and the player received a
    /// second copy of something they had already used. A duplicated paid item — the same class as the
    /// phase 1 review's C1, but demonstrated rather than theorised.
    ///
    /// See <see cref="WriteAppliedSnapshot_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_LeavesItDeleted"/>
    /// for the same race against the write path as it now stands.
    /// </summary>
    [Fact]
    public async Task Store_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_ResurrectsIt()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // Reader A takes its copy before anything is deleted — the snapshot path's LoadManyAsync.
        ItemInstance copyLoadedBeforeTheDelete;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            copyLoadedBeforeTheDelete = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
            Assert.True(copyLoadedBeforeTheDelete.PendingSpawn);
        }

        // Writer B soft-deletes and commits, inside A's load-to-save window.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            Assert.Null(await repository.FindByIdAsync(instance.Id, CancellationToken.None));
        }

        // A now writes its stale copy back, exactly as an applied snapshot upsert would.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(copyLoadedBeforeTheDelete);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();

        var live = await session.Query<ItemInstance>().Where(x => x.Id == instance.Id).SingleOrDefaultAsync();

        // This is the observed behaviour, asserted so it cannot change unnoticed — not the behaviour
        // this codebase wants.
        Assert.NotNull(live);
        Assert.True(live.PendingSpawn);
    }

    /// <summary>
    /// The regression test for the conversion: the exact race the characterisation test above
    /// demonstrates, run against the write the snapshot path actually performs, must leave the row
    /// <b>deleted</b>.
    ///
    /// A patch is an <c>UPDATE</c>. Against a row another writer soft-deleted in this batch's
    /// load-to-save window it matches nothing and writes nothing, so the delete wins — which is the
    /// correct outcome, not merely a safer one. The consumed item stays consumed, and nothing returns
    /// to the delivery queue.
    /// </summary>
    [Fact]
    public async Task WriteAppliedSnapshot_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_LeavesItDeleted()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        ItemInstance copyLoadedBeforeTheDelete;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            copyLoadedBeforeTheDelete = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // The batch decides to apply its upsert against the copy it loaded, unaware of the delete.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            copyLoadedBeforeTheDelete.ApplySnapshot(
                7, ParentKind.Character, owner, null, "chest", null, 0.9f, 12, ItemAttributes.Empty, DateTimeOffset.UtcNow);
            copyLoadedBeforeTheDelete.RewriteResolvedRoots(owner, new GameServerId(Guid.NewGuid()), null, DateTimeOffset.UtcNow);
            repository.WriteAppliedSnapshot(copyLoadedBeforeTheDelete);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();

        Assert.Null(await session.Query<ItemInstance>().Where(x => x.Id == instance.Id).SingleOrDefaultAsync());

        var tombstoned = await session.Query<ItemInstance>()
            .Where(x => x.Id == instance.Id && x.MaybeDeleted())
            .SingleOrDefaultAsync();
        Assert.NotNull(tombstoned);

        // The assertion that actually distinguishes "the UPDATE matched nothing" from "the UPDATE
        // landed on a row that happened to already be deleted". The patch above set Revision to 7; if
        // it had applied to the tombstone — which it would if Marten's patch ignored the soft-delete
        // filter the way its upsert ignores the marker — the row would read 7 here while still being
        // invisible to the live query, and every other assertion in this test would pass regardless.
        Assert.Equal(0, tombstoned.Revision);

        // And still not pending, so nothing re-offers it even if it is ever restored.
        Assert.False(tombstoned.PendingSpawn);
    }

    /// <summary>
    /// The ack path's version of the same race, and the reason it was worth closing there too: an ack
    /// arrives late <i>by design</i> — the Bridge is store-and-forward with Polly retries — so its
    /// load-to-save window is the widest in the module. The concrete case is a granted item consumed
    /// before its ack ever landed: the snapshot delete correctly removes it, and then the delayed ack
    /// arrives. With a whole-document <c>Store()</c> that put an already-used item back into live
    /// inventory; with a patch the delete wins.
    /// </summary>
    [Fact]
    public async Task WriteAcknowledgedSpawn_OfACopyLoadedBeforeAnotherWriterSoftDeletedTheRow_LeavesItDeleted()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // The ack handler loads the still-pending row it is about to acknowledge.
        ItemInstance copyLoadedBeforeTheDelete;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            copyLoadedBeforeTheDelete = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
            Assert.True(copyLoadedBeforeTheDelete.PendingSpawn);
            Assert.Null(copyLoadedBeforeTheDelete.RootGameServerId);
        }

        // Meanwhile a snapshot reports the item as consumed and deletes it.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = (await repository.FindByIdAsync(instance.Id, CancellationToken.None))!;
            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        var ackServerId = new GameServerId(Guid.NewGuid());
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            copyLoadedBeforeTheDelete.AcknowledgeSpawn(ackServerId, DateTimeOffset.UtcNow);
            repository.WriteAcknowledgedSpawn(copyLoadedBeforeTheDelete);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();

        Assert.Null(await session.Query<ItemInstance>().Where(x => x.Id == instance.Id).SingleOrDefaultAsync());

        var tombstoned = await session.Query<ItemInstance>()
            .Where(x => x.Id == instance.Id && x.MaybeDeleted())
            .SingleOrDefaultAsync();
        Assert.NotNull(tombstoned);

        // The discriminating field: PendingSpawn is false either way (SoftDelete clears it too), but
        // only the ack stamps a delivery server. Still null means the patch wrote nothing at all.
        Assert.Null(tombstoned.RootGameServerId);
    }

    [Fact]
    public async Task Store_ThenSaveChanges_PersistsEveryFieldNeededToReconstructTheInstance()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var reloaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal(instance.ItemId, reloaded.ItemId);
            Assert.Equal(instance.Origin, reloaded.Origin);
            Assert.Equal(instance.OriginRef, reloaded.OriginRef);
            Assert.Equal(instance.RegisteredAt, reloaded.RegisteredAt);
            Assert.Equal(instance.RootCharacterId, reloaded.RootCharacterId);
            Assert.True(reloaded.PendingSpawn);
            Assert.Equal(0, reloaded.Revision);
            Assert.Empty(reloaded.Attributes.Values);
        }
    }

    [Fact]
    public async Task FindByRootCharacterAsync_ReturnsOnlyInstancesOwnedByThatCharacter()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var somebodyElse = new CharacterId(Guid.NewGuid());
        var first = Register(owner);
        var second = Register(owner);
        var notOwned = Register(somebodyElse);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(first);
            repository.Store(second);
            repository.Store(notOwned);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var found = await repository.FindByRootCharacterAsync(owner, CancellationToken.None);

            Assert.Equal(2, found.Count);
            Assert.Contains(found, x => x.Id == first.Id);
            Assert.Contains(found, x => x.Id == second.Id);
            Assert.DoesNotContain(found, x => x.Id == notOwned.Id);
        }
    }

    [Fact]
    public async Task LoadManyAsync_ReturnsExactlyTheRequestedIdsAndNoOthers()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var first = Register(owner);
        var second = Register(owner);
        var third = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(first);
            repository.Store(second);
            repository.Store(third);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var found = await repository.LoadManyAsync([first.Id, third.Id], CancellationToken.None);

            Assert.Equal(2, found.Count);
            Assert.Contains(found, x => x.Id == first.Id);
            Assert.Contains(found, x => x.Id == third.Id);
            Assert.DoesNotContain(found, x => x.Id == second.Id);
        }
    }

    [Fact]
    public async Task SoftDelete_ThenSaveChanges_RemovesTheInstanceFromTheRootCharacterRead()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var found = await repository.FindByRootCharacterAsync(owner, CancellationToken.None);

            Assert.DoesNotContain(found, x => x.Id == instance.Id);
        }
    }

    /// <summary>
    /// Task 5's rule: an explicit delete of a still-pending row must clear PendingSpawn on the way
    /// out, not just soft-delete it. PendingSpawn only ever protects a row from *reconcile* (its
    /// absence from a snapshot), never from an explicit "this is gone" — the common cause being a
    /// granted item consumed before its ack lands. Getting this wrong would re-spawn the (correctly)
    /// deleted item at the character's next login. Reaches past the domain with Marten's
    /// MaybeDeleted() to inspect the soft-deleted row's own field, the same reasoning
    /// InventoryReadsTests documents for its Patch usage — this is the only way to see a soft-deleted
    /// row's data at all, since every other read excludes it by default.
    /// </summary>
    [Fact]
    public async Task ExplicitDelete_OfAPendingInstance_ClearsPendingAsItDeletes()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.True(loaded.PendingSpawn);

            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();
        var maybeDeleted = await session.Query<ItemInstance>()
            .Where(x => x.Id == instance.Id && x.MaybeDeleted())
            .SingleOrDefaultAsync();

        Assert.NotNull(maybeDeleted);
        Assert.False(maybeDeleted.PendingSpawn);
    }

    /// <summary>
    /// Review round 1, B-1: proves the partial unique index on (ContainerInstanceId, Slot) — see
    /// ServiceCollectionExtensions.cs — and MartenItemInstanceRepository.SaveChangesAsync's translation
    /// of the resulting 23505 into ChildSlotAlreadyMintedException, deterministically. Two separate
    /// sessions each mint a child for the same parent+slot; the first to save wins, the second must
    /// fail loudly rather than silently double-mint. AcknowledgeSpawnsTests covers the higher-level
    /// behaviour (the handler reconciling this into a normal Minted outcome); this is the mechanism it
    /// depends on.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_ForConcurrentChildInsertsAtTheSameParentAndSlot_TheLoserThrowsChildSlotAlreadyMinted()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var parent = Register(owner);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(parent);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        var childItemId = new ItemId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await using var scopeA = _provider.CreateAsyncScope();
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var childA = ItemInstance.RegisterChild(new ItemInstanceId(Guid.NewGuid()), childItemId, parent, "mag-1", now);
        repositoryA.Store(childA);
        await repositoryA.SaveChangesAsync(CancellationToken.None);

        await using var scopeB = _provider.CreateAsyncScope();
        var repositoryB = scopeB.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var childB = ItemInstance.RegisterChild(new ItemInstanceId(Guid.NewGuid()), childItemId, parent, "mag-1", now);
        repositoryB.Store(childB);

        await Assert.ThrowsAsync<ChildSlotAlreadyMintedException>(
            () => repositoryB.SaveChangesAsync(CancellationToken.None).AsTask());
    }

    /// <summary>
    /// Whole-branch review finding C1, reproduced against real Postgres before it was fixed:
    /// <c>ItemInstance</c> carries no optimistic concurrency, so a whole-document
    /// <c>repository.Store(...)</c> of a counter-only change writes every other field of the caller's
    /// (possibly stale) copy back over whatever committed in the meantime.
    ///
    /// The scenario is ordinary, not exotic — the Bridge is store-and-forward with Polly retries, so a
    /// duplicated <c>GET /pending</c> is expected: a retried read loads the row while it is still
    /// pending, the ack from the first read commits (<c>PendingSpawn = false</c>,
    /// <c>RootGameServerId</c> stamped), and the retried handler then saves its stale copy. With
    /// <c>Store</c> that put <c>PendingSpawn = true</c> and <c>RootGameServerId = null</c> back, the row
    /// was offered again at the next <c>/pending</c>, and the player ended up holding two physical
    /// copies of one purchased item. With <c>RecordDeliveryAttempt</c>'s patch, only
    /// <c>DeliveryAttempts</c> and <c>UpdatedAt</c> are written, so the ack survives.
    /// </summary>
    [Fact]
    public async Task RecordDeliveryAttempt_ForAnInstanceAckedConcurrently_DoesNotResurrectPendingSpawn()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);
        var serverId = new GameServerId(Guid.NewGuid());

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // The retried GET /pending loads the row while it is still pending.
        await using var staleScope = _provider.CreateAsyncScope();
        var staleRepository = staleScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stale = await staleRepository.FindByIdAsync(instance.Id, CancellationToken.None);
        Assert.NotNull(stale);
        Assert.True(stale.PendingSpawn);

        // The ack from the first read commits, clearing PendingSpawn and stamping the delivery server.
        await using (var ackScope = _provider.CreateAsyncScope())
        {
            var repository = ackScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var fresh = await repository.FindByIdAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(fresh);
            fresh.AcknowledgeSpawn(serverId, DateTimeOffset.UtcNow);
            repository.Store(fresh);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // The retried handler now records its delivery attempt against the stale copy.
        staleRepository.RecordDeliveryAttempt(stale, DateTimeOffset.UtcNow);
        await staleRepository.SaveChangesAsync(CancellationToken.None);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var reloaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.False(reloaded.PendingSpawn);
            Assert.Equal(serverId, reloaded.RootGameServerId);
            // The counter still moved — the patch is a real write, not a no-op.
            Assert.Equal(1, reloaded.DeliveryAttempts);
        }
    }

    /// <summary>
    /// The negative-ack half of finding C1 — <c>SpawnFailedHandler</c> reads the row, then writes its
    /// failure reason/timestamp/count, and a concurrent ack can land in that window. Same patch-not-Store
    /// requirement as <see cref="RecordDeliveryAttempt_ForAnInstanceAckedConcurrently_DoesNotResurrectPendingSpawn"/>.
    /// </summary>
    [Fact]
    public async Task RecordSpawnFailure_ForAnInstanceAckedConcurrently_DoesNotResurrectPendingSpawn()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner);
        var serverId = new GameServerId(Guid.NewGuid());

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var staleScope = _provider.CreateAsyncScope();
        var staleRepository = staleScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stale = await staleRepository.FindByIdAsync(instance.Id, CancellationToken.None);
        Assert.NotNull(stale);

        await using (var ackScope = _provider.CreateAsyncScope())
        {
            var repository = ackScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var fresh = await repository.FindByIdAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(fresh);
            fresh.AcknowledgeSpawn(serverId, DateTimeOffset.UtcNow);
            repository.Store(fresh);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        staleRepository.RecordSpawnFailure(stale, SpawnFailureReason.InventoryFull, DateTimeOffset.UtcNow);
        await staleRepository.SaveChangesAsync(CancellationToken.None);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var reloaded = await repository.FindByIdAsync(instance.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.False(reloaded.PendingSpawn);
            Assert.Equal(serverId, reloaded.RootGameServerId);
            Assert.Equal(SpawnFailureReason.InventoryFull, reloaded.LastSpawnFailureReason);
            Assert.Equal(1, reloaded.SpawnFailureCount);
            Assert.NotNull(reloaded.LastSpawnFailureAt);
        }
    }
}
