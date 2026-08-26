using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class here. Mediator.SourceGenerator builds one static
/// dispatch table per compiled assembly from a single scan of AddMediator's options.Assemblies, so
/// there must be exactly one call site no matter how many test classes need a provider — a second
/// one fails the build with MSG0007. See ARCHITECTURE.md §9e.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider()
    {
        var database = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PhoneDatabase"] = database,
                ["ConnectionStrings:AccountDatabase"] = database,
                // Unused by the hive-settings path, but AddAccountInfrastructure wires the Keycloak
                // provisioning client up front and will not resolve without them.
                ["Keycloak:BaseUrl"] = Env("ELIFERPG_TEST_KEYCLOAK_URL", "http://keycloak:8080/"),
                ["Keycloak:Realm"] = "eliferpg",
                ["Keycloak:ProvisioningClientId"] = "account-service",
                ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies =
            [
                typeof(ELifeRPG.Phone.Application.AssemblyMarker),
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddPhoneInfrastructure(configuration);
        services.AddAccountInfrastructure(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The defaults are the devcontainer's view of the Compose stack (README.md). The env overrides
    // exist so the same tests can run from the host, where Postgres is published on 5433.
    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}
