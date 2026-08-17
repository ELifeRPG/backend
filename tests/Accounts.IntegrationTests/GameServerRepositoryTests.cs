using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class GameServerRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task GetOrDefaultAsync_NoRecord_ReturnsWhitelistDisabled()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var clientId = $"never-configured-{Guid.NewGuid()}";

        var server = await repository.GetOrDefaultAsync(clientId, CancellationToken.None);

        Assert.Equal(clientId, server.ClientId);
        Assert.False(server.WhitelistEnabled);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetOrDefaultAsync_RoundTrips()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGameServerRepository>();
        var clientId = $"toggle-test-{Guid.NewGuid()}";

        await repository.UpsertAsync(new GameServer { ClientId = clientId, WhitelistEnabled = true }, CancellationToken.None);
        var server = await repository.GetOrDefaultAsync(clientId, CancellationToken.None);

        Assert.True(server.WhitelistEnabled);
    }
}
