using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Infrastructure.Common;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class in this project. Mediator.SourceGenerator builds
/// one static dispatch table per compiled assembly from a single scan of AddMediator's
/// options.Assemblies configuration — having two separate `AddMediator(...)` call sites (e.g. one
/// per test class) fails to build with "MSG0007: Assemblies can only be configured once", even
/// though each call site listed the identical assembly list. Centralizing here keeps it to exactly
/// one call site regardless of how many test classes need a provider. See ARCHITECTURE.md §9e.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(bool withInfrastructure = false)
    {
        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ELifeRPG.Accounts.Application.AssemblyMarker)];
            // Handlers depend on Marten's scoped IDocumentSession — see src/Api/Program.cs for why
            // this matters (BuildServiceProvider() here doesn't validate scopes by default, so this
            // project wouldn't actually fail without it, but Singleton handlers would silently pin a
            // stale IDocumentSession across every call).
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();

        if (withInfrastructure)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                    ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
                    ["Keycloak:Realm"] = "eliferpg",
                    ["Keycloak:ProvisioningClientId"] = "account-service",
                    ["Keycloak:ProvisioningClientSecret"] = "account-service-secret",
                })
                .Build();
            services.AddAccountInfrastructure(configuration);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
