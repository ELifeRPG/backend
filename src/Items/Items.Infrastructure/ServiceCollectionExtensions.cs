using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Infrastructure.Common;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ItemInfrastructureExtensions
{
    public static IServiceCollection AddItemInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<IItemsStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("ItemDatabase")!);
            options.Events.DatabaseSchemaName = "items";
            options.DatabaseSchemaName = "items";
            options.Projections.Add<ItemProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<IItemRepository, MartenItemRepository>();

        return services;
    }
}
