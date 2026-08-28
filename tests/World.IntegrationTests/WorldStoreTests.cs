using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`).
/// </summary>
public sealed class WorldStoreTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // This is Global Constraint 1's enforcement, and the most important test in the module:
    // ItemInstance is a plain Marten document, never a projection. Under a snapshot/last-write-wins
    // model the game sends a full set rather than a delta, and full-world persistence eventually
    // requires pruning, which would permanently break projection rebuild. A future change that registers
    // ItemInstance as a projection (Snapshot<ItemInstance>, a SingleStreamProjection, etc.) must fail
    // this test.
    [Fact]
    public async Task ItemInstance_IsNotRegisteredAsAProjection()
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();

        // IDocumentStore.Options is typed as the read-only IReadOnlyStoreOptions, which does not
        // expose Projections — the concrete runtime type is always StoreOptions, so the cast is safe.
        var options = (StoreOptions)store.Options;

        Assert.DoesNotContain(typeof(ItemInstance), options.Projections.AllAggregateTypes());
    }

    /// <summary>
    /// Global constraint 1, over every other document this module stores: <see cref="AppliedBatch"/>
    /// and <see cref="ScopeCursor"/> (task 3), <see cref="SuspiciousReconcile"/> (task 4), and
    /// <see cref="UnknownPrefabSighting"/> (task 5) are plain documents too, exactly like
    /// <see cref="ItemInstance"/> above — a last-write-once idempotency cache, a monotonic counter, a
    /// refusal recorded once, and a running tally, none of them with history worth replaying through
    /// event sourcing. <see cref="WorldSettings"/> is here for completeness: it is the module's settings
    /// singleton and has never been anything else.
    /// </summary>
    [Fact]
    public async Task EveryOtherWorldDocument_IsNotRegisteredAsAProjection()
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        var options = (StoreOptions)store.Options;

        var aggregateTypes = options.Projections.AllAggregateTypes();
        Assert.DoesNotContain(typeof(AppliedBatch), aggregateTypes);
        Assert.DoesNotContain(typeof(ScopeCursor), aggregateTypes);
        Assert.DoesNotContain(typeof(SuspiciousReconcile), aggregateTypes);
        Assert.DoesNotContain(typeof(WorldSettings), aggregateTypes);
        Assert.DoesNotContain(typeof(UnknownPrefabSighting), aggregateTypes);
    }

    [Fact]
    public async Task ItemInstance_RoundTripsThroughTheStore()
    {
        var instance = ItemInstance.Register(
            new ItemInstanceId(Guid.NewGuid()),
            new ItemId(Guid.NewGuid()),
            new CharacterId(Guid.NewGuid()),
            ItemOrigin.ShopPurchase,
            new OriginRef("Shops", Guid.NewGuid().ToString()),
            DateTimeOffset.UtcNow);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Store(instance);
            await session.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.QuerySession();
            var reloaded = await session.LoadAsync<ItemInstance>(instance.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal(instance.Id, reloaded.Id);
        }
    }
}
