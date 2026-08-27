using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). There is no HTTP-level test
/// harness in this repo (see every other *.IntegrationTests project's TestServices), so this covers
/// the same path the `GET /api/inventory/limits` endpoint dispatches through
/// (<c>WorldSettingsQuery</c>) plus the domain constants it composes alongside the settings.
/// </summary>
public sealed class WorldSettingsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task WorldSettingsQuery_WithNoStoredDocument_ReturnsThePhase1DefaultValues()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var settings = await mediator.Send(new WorldSettingsQuery());

        Assert.Equal(100, settings.MaxInstancesPerGrant);
        Assert.Equal(3600, settings.GroundItemTtlSeconds);
        Assert.Equal(50, settings.MaxPendingPageSize);
        Assert.Equal(3, settings.MaxDeliveryAttempts);
    }

    [Fact]
    public void StructuralDomainConstants_ComposedIntoTheLimitsResponse_MatchThePhase1Values()
    {
        // These are domain constants, not WorldSettings fields — see the phase 1 task brief's
        // Controller ruling. The limits endpoint (World.Api's WorldLimitsDto) reads them directly.
        Assert.Equal(6, ItemInstance.MaxContainerDepth);
        Assert.Equal(16, ItemAttributes.MaxKeys);
        Assert.Equal(256, ItemAttributes.MaxValueLength);
    }
}
