using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Infrastructure.Common;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shared.Infrastructure;
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
            options.UseSystemTextJsonWithPrivateSetters();
            options.RegisterStronglyTypedIds(typeof(ELifeRPG.Shops.Domain.ShopId), typeof(ELifeRPG.Shops.Domain.ShopListingId));

            options.Connection(configuration.GetConnectionString("ShopDatabase")!);
            options.Events.DatabaseSchemaName = "shops";
            options.DatabaseSchemaName = "shops";
            options.Projections.Add<ShopProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
            options.Projections.Add<ShopListingProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<IShopRepository, MartenShopRepository>();
        services.TryAddScoped<IShopListingRepository, MartenShopListingRepository>();
        services.TryAddScoped<ITransactionParticipant<IShopListingRepository>, MartenShopListingParticipant>();

        return services;
    }
}
