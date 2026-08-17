using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Items.IntegrationTests;

internal static class TestServices
{
    public static ServiceProvider BuildProvider(string gameServerClientId = "gameserver-dev")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ItemDatabase"] = "Host=postgres;Database=postgres;Username=postgres;Password=supersecret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ELifeRPG.Items.Application.AssemblyMarker)];
            options.ServiceLifetime = ServiceLifetime.Transient;
        });
        services.AddItemInfrastructure(configuration);
        services.AddScoped<ELifeRPG.Items.Application.Common.ICurrentGameServer>(_ => new FixedCurrentGameServer(gameServerClientId));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FixedCurrentGameServer(string clientId) : ELifeRPG.Items.Application.Common.ICurrentGameServer
{
    public string ClientId { get; } = clientId;
}
