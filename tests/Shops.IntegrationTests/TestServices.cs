using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class in this project — see ARCHITECTURE.md §9e.
/// Parameterized by the gameserver client id so tests can build two providers reporting as
/// different calling servers — Shop/ShopListing data is hive-wide (no longer tenanted), so this now
/// exists to prove cross-server *visibility*, not isolation. Matches
/// Banking.IntegrationTests/Companies.IntegrationTests' TestServices.cs.
///
/// The optional <paramref name="configureServices"/> hook is applied last, after every
/// AddXInfrastructure call, so a caller's override always wins over the real registration rather than
/// being clobbered by it. Ported from World.IntegrationTests' TestServices.cs for the same reason it
/// exists there: one test (PurchaseListing_WhenTheGrantFails_RollsBackThePayment) swaps in a faulty
/// IItemInstanceRepositoryFactory to fault *inside* PurchaseListingHandler's open cross-module
/// transaction. Every other call site passes null and gets the same provider as before.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(
        string gameServerClientId = "gameserver-dev",
        Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AccountDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:CharacterDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:BankingDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:CompanyDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:ItemDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:ShopDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:WorldDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
                ["ConnectionStrings:SharedDatabase"] = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret"),
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
                typeof(ELifeRPG.Banking.Application.AssemblyMarker),
                typeof(ELifeRPG.Companies.Application.AssemblyMarker),
                typeof(ELifeRPG.Items.Application.AssemblyMarker),
                typeof(ELifeRPG.Shops.Application.AssemblyMarker),
                // Task 6: PurchaseListingHandler grants into World atomically with the payment via
                // IItemInstanceRepositoryFactory (a plain DI service, not a Mediator dispatch), but it
                // also dispatches World.Application's own public WorldSettingsQuery via IMediator for
                // its pre-transaction grant-size cap check — the sanctioned Application->Application
                // borrow (a direct IWorldSettingsRepository injection is not, per review). This
                // AssemblyMarker is required today, not just future-proofing: this project's own
                // PurchaseListing_ExceedingMaxInstancesPerGrant_IsRejectedBeforeAnyPaymentMoves test
                // also dispatches WorldSettingsQuery directly through the same IMediator.
                typeof(ELifeRPG.World.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddAccountInfrastructure(configuration);
        services.AddSingleton<TestCurrentKeycloakUser>();
        services.AddSingleton<ELifeRPG.Accounts.Application.Common.ICurrentKeycloakUser>(
            sp => sp.GetRequiredService<TestCurrentKeycloakUser>());
        services.AddCharacterInfrastructure(configuration);
        services.AddBankingInfrastructure(configuration);
        services.AddCompanyInfrastructure(configuration);
        services.AddItemInfrastructure(configuration);
        services.AddShopInfrastructure(configuration);
        services.AddWorldInfrastructure(configuration);
        services.AddCrossModuleIntegration(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.Shops.Application.Common.ICurrentGameServer>(_ => fake);

        // Applied last, after every AddXInfrastructure call above — see this class's doc comment.
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The defaults are the devcontainer's view of the Compose stack (README.md). The env overrides
    // exist so the same tests can run from the host, or against a stack on remapped ports, without
    // editing this file.
    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}

// Banking and Companies no longer have their own ICurrentGameServer (deleted outright); Characters'
// and Shops' both survive, reshaped to resolve a durable GameServerId instead of keying tenancy.
internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer,
    ELifeRPG.Shops.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;

    // Neither Characters' nor Shops' ICurrentGameServer keys tenancy anymore — both resolve a
    // durable GameServerId. Deterministic per client id so this fake stays consistent with itself;
    // nothing in either module validates registry membership. See
    // Characters.IntegrationTests/TestServices.cs.
    public ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(new GameServerId(DeterministicGuid(ClientId)));

    private static Guid DeterministicGuid(string value)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
