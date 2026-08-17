using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Infrastructure.Common;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ShopInfrastructureExtensions
{
    public static IServiceCollection AddShopInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<IShopsStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("ShopDatabase")!);
            options.Events.DatabaseSchemaName = "shops";
            options.DatabaseSchemaName = "shops";
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Add<ShopProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
            options.Projections.Add<ShopListingProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<IShopRepository, MartenShopRepository>();
        services.TryAddScoped<IShopListingRepository, MartenShopListingRepository>();
        services.TryAddScoped<IShopListingRepositoryFactory, MartenShopListingRepositoryFactory>();

        return services;
    }
}
