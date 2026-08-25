using ELifeRPG.Accounts.Application.Hive;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// HiveSettings is a singleton row on the shared, persistent test database — every test that
/// mutates it collides with every other test that reads or writes it. This collection forces
/// xUnit to serialize this class against <see cref="CreateSessionCommandWhitelistGateTests"/>
/// (which Task 5 populates with tests that read WhitelistEnabled), so the two can never
/// interleave and observe each other's in-flight mutation.
/// </summary>
[CollectionDefinition("HiveSettings")]
public sealed class HiveSettingsCollection;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) — see README.md.
/// </summary>
[Collection("HiveSettings")]
public class HiveSettingsTests
{
    private readonly ServiceProvider _provider = TestServices.BuildProvider(withInfrastructure: true);

    [Fact]
    public async Task Update_ThenQuery_ReturnsTheUpdatedValue()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: true), CancellationToken.None);
            var settings = await mediator.Send(new HiveSettingsQuery(), CancellationToken.None);

            Assert.True(settings.WhitelistEnabled);
        }
        finally
        {
            // Restore, so this test doesn't leak state into the whitelist tests — HiveSettings is a
            // singleton row on the shared database, so this must run even if the assertion above (or
            // anything before it) throws.
            await mediator.Send(new UpdateHiveSettingsCommand(WhitelistEnabled: false), CancellationToken.None);
        }

        var restored = await mediator.Send(new HiveSettingsQuery(), CancellationToken.None);
        Assert.False(restored.WhitelistEnabled);
    }
}
