using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Microsoft.Extensions.Configuration;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SharedIntegrationExtensions
{
    public static IServiceCollection AddCrossModuleIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SharedDatabase")!;
        services.AddSingleton<ICrossModuleTransactionFactory>(new CrossModuleTransactionFactory(connectionString));
        return services;
    }
}
