using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Shared DI/Mediator setup for every test class here. Mediator.SourceGenerator builds one static
/// dispatch table per compiled assembly from a single scan of AddMediator's options.Assemblies, so
/// there must be exactly one call site no matter how many test classes need a provider — a second
/// one fails the build with MSG0007. See ARCHITECTURE.md §9e.
///
/// Also wires up the Items module (task 3): the grant path resolves a granted item's PrefabClassName
/// through Items.Application's batched ItemCatalogEntriesQuery contract, dispatched via IMediator, so
/// a test that grants an item needs a real catalog entry to resolve against.
///
/// Task 5 adds Accounts and Characters: the ack/spawn-failed server guard dispatches Characters.Application's
/// batched CharactersOnServerQuery contract, which needs a real character (with a real CurrentServerId)
/// to test against, and CreateCharacterCommand itself dispatches Accounts.Application's
/// AccountLookupQuery, so creating a test character needs a real account. Parameterized by the
/// gameserver client id, same as Shops/Characters/Banking's TestServices.cs, so a test can build two
/// providers reporting as different calling servers to exercise the guard's rejection path.
///
/// Task 7 adds AddCrossModuleIntegration (and a SharedDatabase connection string): GatherCommand
/// orchestrates one ICrossModuleTransaction across Characters and World, same wiring
/// Shops.IntegrationTests' TestServices.cs already has for PurchaseListingCommand.
///
/// Task 7 review round 1 adds the optional <paramref name="configureServices"/> hook: one test
/// (Gather_WhenTheItemGrantLegFailsAfterSkillXpFlushed_RollsBackTheSkillXp) needs to swap in a
/// hand-written faulty item-instance participant to prove GatherHandler's own cross-module
/// rollback, without duplicating this whole method's wiring just for that one substitution. Every
/// other call site passes null and gets the exact same provider as before this hook existed.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev", Action<IServiceCollection>? configureServices = null)
    {
        var database = Env("ELIFERPG_TEST_DB", "Host=postgres;Database=postgres;Username=postgres;Password=supersecret");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorldDatabase"] = database,
                ["ConnectionStrings:ItemDatabase"] = database,
                ["ConnectionStrings:AccountDatabase"] = database,
                ["ConnectionStrings:CharacterDatabase"] = database,
                // Task 7: GatherCommand opens an ICrossModuleTransaction across Characters and World.
                ["ConnectionStrings:SharedDatabase"] = database,
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
                typeof(ELifeRPG.World.Application.AssemblyMarker),
                typeof(ELifeRPG.Items.Application.AssemblyMarker),
                typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
                typeof(ELifeRPG.Characters.Application.AssemblyMarker),
            ];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddWorldInfrastructure(configuration);
        services.AddItemInfrastructure(configuration);
        services.AddAccountInfrastructure(configuration);
        services.AddSingleton<TestCurrentKeycloakUser>();
        services.AddSingleton<ELifeRPG.Accounts.Application.Common.ICurrentKeycloakUser>(
            sp => sp.GetRequiredService<TestCurrentKeycloakUser>());
        services.AddCharacterInfrastructure(configuration);
        // Task 7: GatherCommand's ICrossModuleTransactionFactory, same wiring as
        // Shops.IntegrationTests' TestServices.cs.
        services.AddCrossModuleIntegration(configuration);

        var fake = new FixedCurrentGameServer(gameServerClientId);
        services.AddScoped<ELifeRPG.Characters.Application.Common.ICurrentGameServer>(_ => fake);
        services.AddScoped<ELifeRPG.World.Application.Common.ICurrentGameServer>(_ => fake);

        // Applied last, after every AddXInfrastructure call above, so a caller's override (e.g.
        // replacing ITransactionParticipant<IItemInstanceRepository> with a faulty fake) always wins over the real
        // registration rather than being clobbered by it.
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The defaults are the devcontainer's view of the Compose stack (README.md). The env overrides
    // exist so the same tests can run from the host, or against a stack on remapped ports, without
    // editing this file.
    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}

// Bypasses the registry lookup (RegistryCurrentGameServer/GameServerIdByClientIdQuery) that the real
// endpoints use — no test here needs a registered gameserver row, only a stable, distinguishable
// GameServerId per client id. Implements both modules' ICurrentGameServer, same reasoning as
// Shops.IntegrationTests' FixedCurrentGameServer: Characters' is needed to create a character "on" a
// given server (CreateCharacterCommand stamps CurrentServerId from it), and World's is needed to make
// the ack/spawn-failed handlers report as that same (or, for the rejection tests, a different) server.
internal sealed class FixedCurrentGameServer(string clientId) :
    ELifeRPG.Characters.Application.Common.ICurrentGameServer,
    ELifeRPG.World.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;

    public ValueTask<GameServerId> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(new GameServerId(DeterministicGuid(ClientId)));

    private static Guid DeterministicGuid(string value)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Unwraps <c>AcknowledgeSpawnsResult</c>'s happy case for the many tests that assert per-instance ack
/// outcomes rather than the batch-level <c>batch_too_large</c> rejection. The union was introduced by
/// the whole-branch review's I5 (the ack endpoint was the one uncapped write surface); everything that
/// was asserting against a bare outcome list before then goes through here now.
/// </summary>
internal static class AckResults
{
    public static IReadOnlyList<ELifeRPG.World.Application.Inventory.InstanceAckOutcome> Acknowledged(
        ELifeRPG.World.Application.Inventory.AcknowledgeSpawnsResult result)
        => result is ELifeRPG.World.Application.Inventory.AcknowledgeSpawnsResult.Acknowledged acknowledged
            ? acknowledged.Outcomes
            : throw new InvalidOperationException($"Expected Acknowledged, got {result}");
}
