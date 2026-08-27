using ELifeRPG.Items.Application.Items;
using ELifeRPG.Items.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Items.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) — see README.md.
///
/// Bulk import is the day-one catalog bootstrap: because the World module refuses to persist an
/// uncatalogued prefab, a world persists nothing at all until a prefab dump has been imported. It
/// therefore has to be safely re-runnable against a catalog that is already partly populated.
/// </summary>
public sealed class BulkImportItemsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static string Prefab(string label) => $"ELRPG_Bulk_{label}_{Guid.NewGuid():N}";

    [Fact]
    public async Task BulkImport_WithNewPrefabs_CreatesThemAll()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var first = Prefab("A");
        var second = Prefab("B");

        var result = await mediator.Send(new BulkImportItemsCommand(
        [
            new BulkImportItem(first, "Alpha"),
            new BulkImportItem(second, "Bravo", ItemPersistence.Persistent),
        ]));

        Assert.True(result is BulkImportItemsResult.Imported, $"Expected Imported, got {result}");
        if (result is not BulkImportItemsResult.Imported imported)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(2, imported.Results.Count);
        Assert.All(imported.Results, x => Assert.True(x.Created));

        var entries = await mediator.Send(new ItemCatalogEntriesQuery(imported.Results.Select(x => x.ItemId).ToList()));
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries.Values, x => x.PrefabClassName == second && x.Persistence == ItemPersistence.Persistent);
    }

    [Fact]
    public async Task BulkImport_WithAnExistingPrefabClassName_DoesNotCreateADuplicate()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefab = Prefab("Existing");

        var firstRun = await mediator.Send(new BulkImportItemsCommand([new BulkImportItem(prefab, "Original")]));
        if (firstRun is not BulkImportItemsResult.Imported first)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        // Re-importing the same dump, this time with a different display name: the existing entry
        // wins untouched, because bulk import registers prefabs and never redefines them.
        var secondRun = await mediator.Send(new BulkImportItemsCommand([new BulkImportItem(prefab, "Renamed")]));

        Assert.True(secondRun is BulkImportItemsResult.Imported, $"Expected Imported, got {secondRun}");
        if (secondRun is not BulkImportItemsResult.Imported second)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.False(second.Results[0].Created);
        Assert.Equal(first.Results[0].ItemId, second.Results[0].ItemId);

        var lookup = await mediator.Send(new ItemLookupQuery(second.Results[0].ItemId));
        if (lookup is ItemLookupResult.Found found)
        {
            Assert.Equal("Original", found.Item.DisplayName);
        }
    }

    [Fact]
    public async Task BulkImport_WithNoDisplayName_FallsBackToThePrefabClassName()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefab = Prefab("NoName");

        var result = await mediator.Send(new BulkImportItemsCommand([new BulkImportItem(prefab)]));
        if (result is not BulkImportItemsResult.Imported imported)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var lookup = await mediator.Send(new ItemLookupQuery(imported.Results[0].ItemId));
        Assert.True(lookup is ItemLookupResult.Found, $"Expected Found, got {lookup}");
        if (lookup is ItemLookupResult.Found found)
        {
            Assert.Equal(prefab, found.Item.DisplayName);
        }
    }

    [Fact]
    public async Task BulkImport_WithTheSamePrefabTwiceInOnePayload_IsRejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefab = Prefab("Twice");

        var result = await mediator.Send(new BulkImportItemsCommand(
        [
            new BulkImportItem(prefab, "First definition"),
            new BulkImportItem(prefab, "Second definition"),
        ]));

        Assert.True(result is BulkImportItemsResult.DuplicateInPayload, $"Expected DuplicateInPayload, got {result}");
        if (result is BulkImportItemsResult.DuplicateInPayload duplicate)
        {
            Assert.Contains(prefab, duplicate.PrefabClassNames);
        }
    }

    [Fact]
    public async Task BulkImport_WithAnEmptyPayload_SucceedsWithoutWriting()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var before = await mediator.Send(new ItemsQuery());
        var result = await mediator.Send(new BulkImportItemsCommand([]));
        var after = await mediator.Send(new ItemsQuery());

        Assert.True(result is BulkImportItemsResult.Imported, $"Expected Imported, got {result}");
        Assert.Equal(before.CatalogVersion, after.CatalogVersion);
    }
}
