using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class in this project — see ARCHITECTURE.md §9e.
/// Parameterized by the gameserver client id so tests can build two independently-tenanted
/// providers to prove per-server isolation of Shop/ShopListing data — matches
/// Banking.IntegrationTests/Companies.IntegrationTests' TestServices.cs (main's tenancy work,
/// merged into this branch).
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
                ["ConnectionStrings:BankingDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:CompanyDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:ItemDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:ShopDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
                ["ConnectionStrings:SharedDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
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
                typeof(ELifeRPG.Banking.Application.AssemblyMarker),
                typeof(ELifeRPG.Companies.Application.AssemblyMarker),
                typeof(ELifeRPG.Items.Application.AssemblyMarker),
                typeof(ELifeRPG.Shops.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddBankingInfrastructure(configuration);
        services.AddCompanyInfrastructure(configuration);
        services.AddItemInfrastructure(configuration);
        services.AddShopInfrastructure(configuration);
        services.AddCrossModuleIntegration(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Banking.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Companies.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Items.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Shops.Application.Common.ICurrentGameServer>(_ => fake);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer,
    ELifeRPG.Banking.Application.Common.ICurrentGameServer,
    ELifeRPG.Companies.Application.Common.ICurrentGameServer,
    ELifeRPG.Items.Application.Common.ICurrentGameServer,
    ELifeRPG.Shops.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
