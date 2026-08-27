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
