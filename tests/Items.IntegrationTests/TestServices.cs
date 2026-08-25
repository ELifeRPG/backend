using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Items.IntegrationTests;

/// <summary>
/// Hive model: the item catalog has no per-gameserver scoping, so there is nothing left to fix here —
/// see docs/superpowers/specs/2026-08-22-hive-tenancy-design.md. BuildProvider still accepts a
/// gameServerClientId parameter (unused) so
/// Handle_ItemCreatedUnderOneServer_IsVisibleFromAnotherServer can build a second, independent
/// provider without needing a real reason to vary it.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        _ = gameServerClientId;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ItemDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ELifeRPG.Items.Application.AssemblyMarker)];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddItemInfrastructure(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The defaults are the devcontainer's view of the Compose stack (README.md). The env overrides
    // exist so the same tests can run from the host, or against a stack on remapped ports, without
    // editing this file.
    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}
