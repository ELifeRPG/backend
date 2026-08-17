using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup — see Banking.IntegrationTests/TestServices.cs for why this needs to be
/// the single AddMediator(...) call site for this compiled test project (Mediator.SourceGenerator
/// rejects a second one). Parameterized by the gameserver client id so tests can build two
/// independently-tenanted providers to prove per-server isolation.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CharacterDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["Keycloak:BaseUrl"] = "http://keycloak:8080/",
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
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(
            _ => new FixedCurrentGameServer(gameServerClientId));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) : ELifeRPG.Characters.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
