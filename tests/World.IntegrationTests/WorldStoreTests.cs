using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Domain.Items;
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
