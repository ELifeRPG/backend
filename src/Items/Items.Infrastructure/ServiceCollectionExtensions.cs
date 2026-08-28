using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain;
using ELifeRPG.Items.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
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
            options.UseSystemTextJsonWithPrivateSetters();

            options.Connection(configuration.GetConnectionString("ItemDatabase")!);
            options.Events.DatabaseSchemaName = "items";
            options.DatabaseSchemaName = "items";
            options.Projections.Add<ItemProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);

            // The World module resolves a Reforger prefab to exactly one catalog entry, so two
            // entries claiming one prefab would make every instance of it ambiguous. Note this index
            // cannot be created against a database that already holds duplicates — a dev volume from
            // before 2026-08-26 may need `docker compose down -v`.
            options.Schema.For<Item>().UniqueIndex(x => x.PrefabClassName);
        });

        services.TryAddScoped<IItemRepository, MartenItemRepository>();

        return services;
    }
}
