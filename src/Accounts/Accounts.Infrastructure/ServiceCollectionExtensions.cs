using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class AccountInfrastructureExtensions
{
    public static IServiceCollection AddAccountInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMarten(options =>
            {
                options.UseSystemTextJsonWithPrivateSetters();
                options.Connection(configuration.GetConnectionString("AccountDatabase")!);
                options.Events.DatabaseSchemaName = "account";
                options.DatabaseSchemaName = "account";
                options.Projections.Add<AccountProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
                options.Projections.Add<WhitelistApplicationProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
                options.Schema.For<GameServer>().Identity(x => x.Id).UniqueIndex(x => x.ClientId);
            });

        services.Configure<KeycloakOptions>(configuration.GetSection("Keycloak"));
        services.AddHttpClient<IKeycloakUserProvisioner, KeycloakUserProvisioner>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeycloakOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        // Talks to the keycloak-bohemia-gameaccount endpoints in the realm, using the same
        // service-account credentials as the provisioner above.
        services.AddHttpClient<IBohemiaGameAccountLinker, KeycloakBohemiaGameAccountLinker>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeycloakOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        services.TryAddScoped<IAccountRepository, MartenAccountRepository>();
        services.TryAddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();
        services.TryAddScoped<IWhitelistApplicationRepository, MartenWhitelistApplicationRepository>();
        services.TryAddScoped<IGameServerRepository, MartenGameServerRepository>();
        services.TryAddScoped<IHiveSettingsRepository, MartenHiveSettingsRepository>();

        return services;
    }
}
