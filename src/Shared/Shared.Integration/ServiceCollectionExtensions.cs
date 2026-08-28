using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public static class SharedIntegrationExtensions
{
    public static IServiceCollection AddCrossModuleIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SharedDatabase")!;

        services.AddScoped<ICrossModuleTransactionFactory>(sp => new CrossModuleTransactionFactory(connectionString, sp));
        return services;
    }
}
