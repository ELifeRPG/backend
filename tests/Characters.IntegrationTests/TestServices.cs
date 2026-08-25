using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup — see Banking.IntegrationTests/TestServices.cs for why this needs to be
/// the single AddMediator(...) call site for this compiled test project (Mediator.SourceGenerator
/// rejects a second one). Parameterized by the gameserver client id so tests can build providers that
/// report as different calling servers.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:CharacterDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
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
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddSingleton<TestCurrentKeycloakUser>();
        services.AddSingleton<ELifeRPG.Accounts.Application.Common.ICurrentKeycloakUser>(
            sp => sp.GetRequiredService<TestCurrentKeycloakUser>());
        services.AddCharacterInfrastructure(configuration);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(
            _ => new FixedCurrentGameServer(gameServerClientId));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The defaults are the devcontainer's view of the Compose stack (README.md). The env overrides
    // exist so the same tests can run from the host, or against a stack on remapped ports, without
    // editing this file.
    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}

internal sealed class FixedCurrentGameServer(string clientId)
    : ELifeRPG.Characters.Application.Common.ICurrentGameServer
{
    // A deterministic id per client id, so two providers built with different client ids get
    // genuinely distinct GameServerIds without each test needing an async registry round-trip in
    // setup. Nothing in the Characters module validates that the id exists in the registry — it
    // only records which server a character is on — so a synthesized id is sufficient here.
    private readonly GameServerId _id = new(DeterministicGuid(clientId));

    public ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_id);

    private static Guid DeterministicGuid(string value)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
