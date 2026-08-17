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
                options.Schema.For<GameServer>().Identity(x => x.ClientId);
            });

        services.Configure<KeycloakOptions>(configuration.GetSection("Keycloak"));
        services.AddHttpClient<IKeycloakUserProvisioner, KeycloakUserProvisioner>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeycloakOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        services.TryAddScoped<IAccountRepository, MartenAccountRepository>();
        services.TryAddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();
        services.TryAddScoped<IWhitelistApplicationRepository, MartenWhitelistApplicationRepository>();
        services.TryAddScoped<IGameServerRepository, MartenGameServerRepository>();

        return services;
    }
}
