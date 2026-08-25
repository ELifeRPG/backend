using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Application.GameServers;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public class GameServerRegistryTests
{
    private readonly ServiceProvider _provider = TestServices.BuildProvider(withInfrastructure: true);

    [Fact]
    public async Task FindByClientId_ForUnregisteredClient_ReturnsNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();

        var found = await repository.FindByClientIdAsync($"never-registered-{Guid.NewGuid()}", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task Upsert_ThenFindByClientId_ReturnsTheServer()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";
        var server = new GameServer
        {
            Id = new GameServerId(Guid.NewGuid()),
            ClientId = clientId,
            DisplayName = "Test Server",
            MapName = "Everon",
        };

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        await repository.UpsertAsync(server, CancellationToken.None);

        var found = await repository.FindByClientIdAsync(clientId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(server.Id, found!.Id);
        Assert.Equal("Everon", found.MapName);
    }

    [Fact]
    public async Task Register_ThenList_IncludesTheServer()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var registered = await mediator.Send(
            new RegisterGameServerCommand(clientId, "Second Server", "Arland"), CancellationToken.None);

        var all = await mediator.Send(new GameServersQuery(), CancellationToken.None);

        Assert.Contains(all, x => x.Id == registered.Id && x.ClientId == clientId);
    }

    [Fact]
    public async Task GameServerIdByClientId_ForUnregisteredClient_ReturnsNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var id = await mediator.Send(
            new GameServerIdByClientIdQuery($"never-registered-{Guid.NewGuid()}"), CancellationToken.None);

        Assert.Null(id);
    }

    [Fact]
    public async Task GameServerIdByClientId_ForRegisteredClient_ReturnsItsId()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var registered = await mediator.Send(
            new RegisterGameServerCommand(clientId, "Resolve Server", "Everon"), CancellationToken.None);

        var id = await mediator.Send(new GameServerIdByClientIdQuery(clientId), CancellationToken.None);

        Assert.Equal(registered.Id, id);
    }

    [Fact]
    public async Task Register_ThenReRegisterSameClientId_UpdatesInPlace()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var first = await mediator.Send(
            new RegisterGameServerCommand(clientId, "Original Name", "Everon"), CancellationToken.None);

        var second = await mediator.Send(
            new RegisterGameServerCommand(clientId, "Renamed Server", "Arland"), CancellationToken.None);

        // The most important assertion: re-registration updates the existing row in place — it
        // must not mint a second server or a new identity for the same client id.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Renamed Server", second.DisplayName);
        Assert.Equal("Arland", second.MapName);

        var all = await mediator.Send(new GameServersQuery(), CancellationToken.None);
        var stored = Assert.Single(all, x => x.ClientId == clientId);
        Assert.Equal(first.Id, stored.Id);
        Assert.Equal("Renamed Server", stored.DisplayName);
        Assert.Equal("Arland", stored.MapName);
    }

    [Fact]
    public async Task GameServerLookup_ForRegisteredClient_ReturnsFound()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var registered = await mediator.Send(
            new RegisterGameServerCommand(clientId, "Lookup Server", "Everon"), CancellationToken.None);

        var result = await mediator.Send(new GameServerLookupQuery(clientId), CancellationToken.None);

        if (result is not GameServerLookupResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {result}.");
        }
        Assert.Equal(registered.Id, found.Server.Id);
        Assert.Equal("Lookup Server", found.Server.DisplayName);
    }

    [Fact]
    public async Task GameServerLookup_ForUnregisteredClient_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(
            new GameServerLookupQuery($"never-registered-{Guid.NewGuid()}"), CancellationToken.None);

        Assert.True(result is GameServerLookupResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task UpdateGameServerSettings_ForRegisteredClient_UpdatesAndReturnsIt()
    {
        var clientId = $"gameserver-{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new RegisterGameServerCommand(clientId, "Before", "Everon"), CancellationToken.None);

        var result = await mediator.Send(
            new UpdateGameServerSettingsCommand(clientId, "After", "Arland"), CancellationToken.None);

        if (result is not UpdateGameServerSettingsResult.Updated updated)
        {
            throw new InvalidOperationException($"Expected Updated, got {result}.");
        }
        Assert.Equal("After", updated.Server.DisplayName);
        Assert.Equal("Arland", updated.Server.MapName);
    }

    [Fact]
    public async Task UpdateGameServerSettings_ForUnregisteredClient_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(
            new UpdateGameServerSettingsCommand($"never-registered-{Guid.NewGuid()}", "Name", "Map"), CancellationToken.None);

        Assert.True(result is UpdateGameServerSettingsResult.NotFound, $"Expected NotFound, got {result}");
    }
}
