using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Items;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 3's grant path —
/// <c>GrantItemsCommand</c> dispatched through Mediator, which is the World-internal entry point onto
/// <c>IItemInstanceRepository.GrantAsync</c> (see that command's doc comment for why task 6/7 don't
/// go through this command themselves).
/// </summary>
public sealed class GrantItemsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static async Task<ItemId> CreateCatalogItemAsync(IMediator mediator, string? prefabClassName = null)
    {
        var result = await mediator.Send(new CreateItemCommand(
            "Test Bandage",
            prefabClassName ?? $"Test_{Guid.NewGuid():N}"));

        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.ItemId;
    }

    [Fact]
    public async Task Grant_ForAQuantity_MintsThatManyDiscreteRowsEachPendingSpawn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefabClassName = $"Test_{Guid.NewGuid():N}";
        var itemId = await CreateCatalogItemAsync(mediator, prefabClassName);
        var owner = new CharacterId(Guid.NewGuid());

        var result = await mediator.Send(new GrantItemsCommand(
            itemId, 10, owner, ItemOrigin.ShopPurchase, new OriginRef("Shops", Guid.NewGuid().ToString())));

        if (result is not GrantItemsResult.Granted grantedResult)
        {
            throw new InvalidOperationException($"Expected Granted, got {result}");
        }

        var granted = grantedResult.Instances;
        Assert.Equal(10, granted.Count);
        Assert.Equal(10, granted.Select(x => x.InstanceId).Distinct().Count());
        Assert.All(granted, x => Assert.Equal(itemId, x.ItemId));
        Assert.All(granted, x => Assert.Equal(prefabClassName, x.PrefabClassName));

        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await repository.LoadManyAsync(granted.Select(x => x.InstanceId).ToList(), CancellationToken.None);
        Assert.Equal(10, stored.Count);
        Assert.All(stored, x => Assert.True(x.PendingSpawn));
        Assert.All(stored, x => Assert.Equal(0, x.Revision));
        Assert.All(stored, x => Assert.Null(x.RootGameServerId));
        Assert.All(stored, x => Assert.Equal(owner, x.RootCharacterId));
    }

    [Fact]
    public async Task Grant_MintedRows_CarryTheOriginAndOriginRefTheyWereGrantedWith()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var itemId = await CreateCatalogItemAsync(mediator);
        var owner = new CharacterId(Guid.NewGuid());
        var originRef = new OriginRef("Shops", Guid.NewGuid().ToString());

        var result = await mediator.Send(new GrantItemsCommand(itemId, 1, owner, ItemOrigin.Gathered, originRef));

        if (result is not GrantItemsResult.Granted grantedResult)
        {
            throw new InvalidOperationException($"Expected Granted, got {result}");
        }

        var granted = grantedResult.Instances[0];

        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await repository.FindByIdAsync(granted.InstanceId, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(ItemOrigin.Gathered, stored.Origin);
        Assert.Equal(originRef, stored.OriginRef);
    }

    [Fact]
    public async Task Grant_ExceedingMaxInstancesPerGrant_IsRejectedAndWritesNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var itemId = await CreateCatalogItemAsync(mediator);
        var owner = new CharacterId(Guid.NewGuid());

        var settings = await mediator.Send(new ELifeRPG.World.Application.Settings.WorldSettingsQuery());
        var tooMany = settings.MaxInstancesPerGrant + 1;

        var result = await mediator.Send(new GrantItemsCommand(
            itemId, tooMany, owner, ItemOrigin.ShopPurchase, null));

        if (result is not GrantItemsResult.QuantityExceedsCap rejected)
        {
            throw new InvalidOperationException($"Expected QuantityExceedsCap, got {result}");
        }

        Assert.Equal(tooMany, rejected.Requested);
        Assert.Equal(settings.MaxInstancesPerGrant, rejected.MaxInstancesPerGrant);

        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var found = await repository.FindByRootCharacterAsync(owner, CancellationToken.None);
        Assert.Empty(found);
    }

    /// <summary>
    /// Task 6/7 call <c>GrantAsync</c> directly through <c>IItemInstanceRepositoryFactory</c>, bypassing
    /// <c>GrantItemsHandler</c> entirely — this covers that they get a named, catchable exception rather
    /// than a bare <see cref="InvalidOperationException"/>, per the phase 1 review's fix-round finding.
    /// </summary>
    [Fact]
    public async Task GrantAsync_ForAnUncatalogedItemId_ThrowsItemNotInCatalog()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var uncatalogedItemId = new ItemId(Guid.NewGuid());
        var owner = new CharacterId(Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ItemNotInCatalogException>(() =>
            repository.GrantAsync(uncatalogedItemId, 1, owner, ItemOrigin.ShopPurchase, null, CancellationToken.None).AsTask());

        Assert.Equal(uncatalogedItemId, exception.ItemId);
    }

    [Fact]
    public async Task Grant_ForAnUncatalogedItemId_MapsToItemNotInCatalogWithoutThrowing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var uncatalogedItemId = new ItemId(Guid.NewGuid());
        var owner = new CharacterId(Guid.NewGuid());

        var result = await mediator.Send(new GrantItemsCommand(
            uncatalogedItemId, 1, owner, ItemOrigin.ShopPurchase, null));

        if (result is not GrantItemsResult.ItemNotInCatalog notInCatalog)
        {
            throw new InvalidOperationException($"Expected ItemNotInCatalog, got {result}");
        }

        Assert.Equal(uncatalogedItemId, notInCatalog.ItemId);
    }
}
