using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Items.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
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

    [Fact]
    public async Task CreateItem_ThenLookup_ReturnsTheSameItem()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateItemCommand("9mm Ammo Box", "Ammo_9x19_Box"));

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
            Assert.Equal("Ammo_9x19_Box", found.Item.PrefabClassName);
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
    public async Task ItemsQuery_ReturnsCreatedItems()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new CreateItemCommand("Bandage", "Medical_Bandage"));
        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var items = await mediator.Send(new ItemsQuery());

        Assert.Contains(items, x => x.Id == created.ItemId);
    }

    [Fact]
    public async Task Item_CreatedOnOneServer_IsInvisibleFromAnotherServer()
    {
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediatorA.Send(new CreateItemCommand("Bandage", "Medical_Bandage"));
        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var lookupFromCreatingServer = await mediatorA.Send(new ItemLookupQuery(created.ItemId));
        var lookupFromOtherServer = await mediatorB.Send(new ItemLookupQuery(created.ItemId));

        Assert.True(lookupFromCreatingServer is ItemLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
        Assert.True(lookupFromOtherServer is ItemLookupResult.NotFound, $"Expected NotFound from a different server, got {lookupFromOtherServer}");
    }
}
