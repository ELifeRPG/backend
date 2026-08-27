using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Marten.Patching;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 4's two join-time
/// reads: <c>CarriedInventoryQuery</c> (backs <c>GET /api/inventory/characters/{id}/items</c> — what a
/// character <b>holds</b>) and <c>PendingDeliveriesQuery</c> (backs
/// <c>GET /api/inventory/characters/{id}/pending</c> — what a character is <b>owed</b>).
///
/// Three scenarios here cannot be produced through any domain method as phase 1 currently stands: an
/// expired or staff-removed row still rooted at a character (see
/// <c>ItemInstance.MoveToContainer</c>'s "forward only" propagation warning and the fact that nothing
/// before task 9's staff tooling ever sets <c>RemovedByStaff</c>), and a <b>delivered</b> row — nothing
/// before task 5's ack endpoint ever clears <c>PendingSpawn</c>, yet the carried-inventory read's whole
/// point is to surface only delivered rows. All three tests reach past the domain API with Marten's
/// <c>Patch</c> feature to stamp the field directly on the stored row, the same way a future feature or
/// a hand-run fixup script would — this is a persistence-layer test, not a statement that the scenario
/// is reachable through today's write paths. <see cref="MarkDeliveredAsync"/> is the shared helper for
/// the third case, used by every fixture that expects a row to surface on the carried-inventory read.
/// </summary>
public sealed class InventoryReadsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static ItemInstance Register(CharacterId owner, DateTimeOffset now)
        => ItemInstance.Register(
            new ItemInstanceId(Guid.NewGuid()),
            new ItemId(Guid.NewGuid()),
            owner,
            ItemOrigin.ShopPurchase,
            new OriginRef("Shops", Guid.NewGuid().ToString()),
            now);

    /// <summary>
    /// Flips a stored row's <c>PendingSpawn</c> to <c>false</c> directly against Marten — standing in
    /// for task 5's not-yet-written ack endpoint, which is the only real write path that will ever do
    /// this. Every carried-inventory fixture in this file needs it: <c>Register</c> always produces
    /// <c>PendingSpawn = true</c>, and the carried-inventory read now (correctly) excludes those rows —
    /// see the fix for phase 1 review round 1's critical finding on <c>FindCarriedByRootCharacterAsync</c>.
    /// </summary>
    private async Task MarkDeliveredAsync(ItemInstanceId id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.LightweightSession();
        session.Patch<ItemInstance>(id.Value).Set(x => x.PendingSpawn, false);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task CarriedInventory_ExcludesSoftDeletedRows()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var kept = Register(owner, now);
        var deleted = Register(owner, now);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(kept);
            repository.Store(deleted);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // Both rows need to be delivered (PendingSpawn = false) so the only remaining difference
        // between them is the soft-delete this test is actually about.
        await MarkDeliveredAsync(kept.Id);
        await MarkDeliveredAsync(deleted.Id);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            var loaded = await repository.FindByIdAsync(deleted.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            repository.SoftDelete(loaded);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var found = await mediator.Send(new CarriedInventoryQuery(owner));

        Assert.Contains(found, x => x.Id == kept.Id);
        Assert.DoesNotContain(found, x => x.Id == deleted.Id);
    }

    [Fact]
    public async Task CarriedInventory_ExcludesExpiredRows()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var notExpired = Register(owner, now);
        var expired = Register(owner, now);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(notExpired);
            repository.Store(expired);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // Both rows need to be delivered so PendingSpawn doesn't also exclude them — this test is
        // about the expiry clause specifically.
        await MarkDeliveredAsync(notExpired.Id);
        await MarkDeliveredAsync(expired.Id);

        // Register() never sets ExpiresAt, and no domain method today can leave an
        // ExpiresAt-carrying row rooted at a character (see class summary) — patch the persisted
        // row directly so the read's lazy expiry filter has something to actually exclude.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(expired.Id.Value).Set(x => x.ExpiresAt, now.AddMinutes(-5));
            await session.SaveChangesAsync();
        }

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var found = await mediator.Send(new CarriedInventoryQuery(owner));

        Assert.Contains(found, x => x.Id == notExpired.Id);
        Assert.DoesNotContain(found, x => x.Id == expired.Id);
    }

    [Fact]
    public async Task CarriedInventory_ExcludesStaffRemovedRows()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var kept = Register(owner, now);
        var removed = Register(owner, now);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(kept);
            repository.Store(removed);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // Both rows need to be delivered so PendingSpawn doesn't also exclude them — this test is
        // about the staff-removed clause specifically.
        await MarkDeliveredAsync(kept.Id);
        await MarkDeliveredAsync(removed.Id);

        // No domain method sets RemovedByStaff before task 9's staff tooling — patch it directly,
        // same reasoning as the expiry test above.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(removed.Id.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var found = await mediator.Send(new CarriedInventoryQuery(owner));

        Assert.Contains(found, x => x.Id == kept.Id);
        Assert.DoesNotContain(found, x => x.Id == removed.Id);
    }

    [Fact]
    public async Task CarriedInventory_IncludesNestedContainerChildren()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var container = Register(owner, now);
        var child = Register(owner, now);

        // Same in-memory resolver shape MoveToContainer expects — see its own doc comment: the
        // caller supplies a lookup over whatever instances are already loaded for this operation.
        child.MoveToContainer(container.Id, "pouch-1", id => id == container.Id ? container : throw new InvalidOperationException(), now);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(container);
            repository.Store(child);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        // Both rows need to be delivered — an undelivered container and its undelivered contents are
        // exactly the "owed" case FindPendingByRootCharacterAsync exists for, not this one.
        await MarkDeliveredAsync(container.Id);
        await MarkDeliveredAsync(child.Id);

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var found = await mediator.Send(new CarriedInventoryQuery(owner));

        Assert.Contains(found, x => x.Id == container.Id);
        var foundChild = Assert.Single(found, x => x.Id == child.Id);
        Assert.Equal(ParentKind.Container, foundChild.ParentKind);
        Assert.Equal(container.Id, foundChild.ContainerInstanceId);
        Assert.Equal(owner, foundChild.RootCharacterId);
    }

    /// <summary>
    /// The regression this whole fix round is about: a freshly granted, still-pending row must appear
    /// on the "owed" read and must not also appear on the "holds" read. Before this fix,
    /// <c>FindCarriedByRootCharacterAsync</c> had no <c>PendingSpawn</c> clause at all, so a mod
    /// following each endpoint's own contract would have spawned every not-yet-acked row twice.
    /// </summary>
    [Fact]
    public async Task PendingRow_AppearsOnPendingRead_AndNotOnCarriedRead()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner, DateTimeOffset.UtcNow);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();

        var carried = await mediator.Send(new CarriedInventoryQuery(owner));
        Assert.DoesNotContain(carried, x => x.Id == instance.Id);

        var pending = await mediator.Send(new PendingDeliveriesQuery(owner, null));
        Assert.Contains(pending, x => x.Id == instance.Id);
    }

    [Fact]
    public async Task PendingDeliveries_HonoursLimitAndOrdersOldestFirst()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-3);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);
        var oldest = Register(owner, t0);
        var middle = Register(owner, t1);
        var newest = Register(owner, t2);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            // Stored out of chronological order on purpose — the read must sort, not rely on
            // insertion order.
            repository.Store(newest);
            repository.Store(oldest);
            repository.Store(middle);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var found = await mediator.Send(new PendingDeliveriesQuery(owner, 2));

        Assert.Equal(2, found.Count);
        Assert.Equal(oldest.Id, found[0].Id);
        Assert.Equal(middle.Id, found[1].Id);
        Assert.DoesNotContain(found, x => x.Id == newest.Id);
    }

    [Fact]
    public async Task PendingDeliveries_ServingARow_IncrementsItsAttemptCount()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner, DateTimeOffset.UtcNow);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var found = await mediator.Send(new PendingDeliveriesQuery(owner, null));
            Assert.Single(found, x => x.Id == instance.Id);
        }

        await using var readScope = _provider.CreateAsyncScope();
        var repository2 = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository2.FindByIdAsync(instance.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded.DeliveryAttempts);
        // DeliveryAttempts is backend-owned and must never be confused with the mod's LWW key.
        Assert.Equal(0, reloaded.Revision);
    }

    [Fact]
    public async Task PendingDeliveries_RowAtTheCap_StopsBeingOffered()
    {
        var owner = new CharacterId(Guid.NewGuid());
        var instance = Register(owner, DateTimeOffset.UtcNow);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
            repository.Store(instance);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        int maxDeliveryAttempts;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            maxDeliveryAttempts = (await mediator.Send(new WorldSettingsQuery())).MaxDeliveryAttempts;
        }

        for (var attempt = 0; attempt < maxDeliveryAttempts; attempt++)
        {
            await using var scope = _provider.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var found = await mediator.Send(new PendingDeliveriesQuery(owner, null));

            Assert.Contains(found, x => x.Id == instance.Id);
        }

        await using var finalScope = _provider.CreateAsyncScope();
        var finalMediator = finalScope.ServiceProvider.GetRequiredService<IMediator>();
        var final = await finalMediator.Send(new PendingDeliveriesQuery(owner, null));

        Assert.DoesNotContain(final, x => x.Id == instance.Id);
    }
}
