using ELifeRPG.Items.Application.Items;
using ELifeRPG.Items.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Items.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
///
/// Prefab class names are unique across the catalog, and these tests run repeatedly against a
/// long-lived database, so every test mints its own prefab name via <see cref="Prefab"/> rather than
/// hardcoding one that would collide with the previous run.
/// </summary>
public sealed class ItemCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static string Prefab(string label) => $"ELRPG_Test_{label}_{Guid.NewGuid():N}";

    [Fact]
    public async Task CreateItem_ThenLookup_ReturnsTheSameItem()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefab = Prefab("AmmoBox");

        var result = await mediator.Send(new CreateItemCommand("9mm Ammo Box", prefab, ItemPersistence.Despawns));

        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var lookup = await mediator.Send(new ItemLookupQuery(created.ItemId));
        Assert.True(lookup is ItemLookupResult.Found, $"Expected Found, got {lookup}");
        if (lookup is ItemLookupResult.Found found)
        {
            Assert.Equal("9mm Ammo Box", found.Item.DisplayName);
            Assert.Equal(prefab, found.Item.PrefabClassName);
            Assert.Equal(ItemPersistence.Despawns, found.Item.Persistence);
        }
    }

    [Fact]
    public async Task CreateItem_WithADuplicatePrefabClassName_IsRejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefab = Prefab("Duplicate");

        var first = await mediator.Send(new CreateItemCommand("First", prefab));
        Assert.True(first is CreateItemResult.Created, $"Expected Created, got {first}");
        if (first is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var second = await mediator.Send(new CreateItemCommand("Second", prefab));

        Assert.True(second is CreateItemResult.DuplicatePrefabClassName, $"Expected DuplicatePrefabClassName, got {second}");
        if (second is CreateItemResult.DuplicatePrefabClassName duplicate)
        {
            Assert.Equal(created.ItemId, duplicate.ExistingItemId);
        }
    }

    [Fact]
    public async Task ItemLookupQuery_ForUnknownId_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ItemLookupQuery(new ItemId(Guid.NewGuid())));

        Assert.True(result is ItemLookupResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task ItemsQuery_ReturnsCreatedItemsAndACatalogVersion()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new CreateItemCommand("Bandage", Prefab("Bandage")));
        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var catalog = await mediator.Send(new ItemsQuery());

        Assert.Contains(catalog.Items, x => x.Id == created.ItemId);
        Assert.True(catalog.CatalogVersion > 0, "Expected a non-zero catalog version.");
    }

    [Fact]
    public async Task ItemsQuery_AfterAnotherItemIsCreated_ReportsAHigherCatalogVersion()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var before = await mediator.Send(new ItemsQuery());
        await mediator.Send(new CreateItemCommand("Version Bump", Prefab("VersionBump")));
        var after = await mediator.Send(new ItemsQuery());

        Assert.True(
            after.CatalogVersion > before.CatalogVersion,
            $"Expected the catalog version to advance, got {before.CatalogVersion} then {after.CatalogVersion}.");
    }

    [Fact]
    public async Task ItemCatalogEntriesQuery_ReturnsOnlyCataloguedIds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new CreateItemCommand("Pickup Truck", Prefab("Truck"), ItemPersistence.Persistent));
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var unknown = new ItemId(Guid.NewGuid());
        var entries = await mediator.Send(new ItemCatalogEntriesQuery([created.ItemId, unknown]));

        // Absence is how "uncatalogued prefabs are not persisted" is enforced on the write path.
        Assert.True(entries.ContainsKey(created.ItemId));
        Assert.False(entries.ContainsKey(unknown));
        Assert.Equal(ItemPersistence.Persistent, entries[created.ItemId].Persistence);
    }

    [Fact]
    public async Task Handle_ItemCreatedUnderOneServer_IsVisibleFromAnotherServer()
    {
        // Hive model: the item catalog is a set of definitions (display name + prefab class), so the
        // same prefab means the same thing on every map. This asserts the opposite of the pre-hive
        // behaviour.
        ItemId itemId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new CreateItemCommand("Bandage", Prefab("HiveWide")), CancellationToken.None);
            Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
            if (result is not CreateItemResult.Created created)
            {
                throw new InvalidOperationException("Unreachable.");
            }

            itemId = created.ItemId;
        }

        await using var otherProvider = TestServices.BuildProvider("gameserver-two");
        await using var otherScope = otherProvider.CreateAsyncScope();
        var otherMediator = otherScope.ServiceProvider.GetRequiredService<IMediator>();

        var found = await otherMediator.Send(new ItemLookupQuery(itemId), CancellationToken.None);

        Assert.True(found is ItemLookupResult.Found, $"Expected Found from a different server, got {found}");
    }
}
