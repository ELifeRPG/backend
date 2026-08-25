using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Companies.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup — see Banking.IntegrationTests/TestServices.cs for why this needs to be
/// the single AddMediator(...) call site for this compiled test project. Parameterized by the
/// gameserver client id so tests can build two providers reporting as different calling servers —
/// Company data is hive-wide (no longer tenanted), so this now exists to prove cross-server
/// *visibility*, not isolation.
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
                ["ConnectionStrings:CompanyDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
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
                typeof(ELifeRPG.Companies.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddCharacterInfrastructure(configuration);
        services.AddCompanyInfrastructure(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

// Companies no longer has its own ICurrentGameServer (deleted outright — see
// docs/superpowers/specs/2026-08-22-hive-tenancy-design.md); Characters' and Shops' both survive,
// reshaped to resolve a durable GameServerId instead of keying tenancy.
internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;

    // Characters' ICurrentGameServer no longer keys tenancy — it resolves a durable GameServerId.
    // Deterministic per client id so this fake stays consistent with itself; nothing in Characters
    // validates registry membership. See Characters.IntegrationTests/TestServices.cs.
    public ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(new GameServerId(DeterministicGuid(ClientId)));

    private static Guid DeterministicGuid(string value)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
