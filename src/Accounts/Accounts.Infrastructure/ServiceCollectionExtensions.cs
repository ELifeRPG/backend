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
                options.RegisterStronglyTypedIds(typeof(KeycloakUserId), typeof(GameIdentityId), typeof(GameAccountLinkId), typeof(WhitelistApplicationId));
                options.Connection(configuration.GetConnectionString("AccountDatabase")!);
                options.Events.DatabaseSchemaName = "account";
                options.DatabaseSchemaName = "account";
                options.Projections.Add<AccountProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
                options.Projections.Add<WhitelistApplicationProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
                options.Schema.For<GameServer>().Identity(x => x.Id).UniqueIndex(x => x.ClientId);

                // The unique index is the whole uniqueness guarantee for game identities: two
                // concurrent player-connected calls for one identity both see "no link", both try to
                // insert, and the second fails here — rolling back the second account with it.
                // Without it the race yields two accounts for one player, silently.
                options.Schema.For<GameAccountLink>().Identity(x => x.Id).UniqueIndex(x => x.GameIdentityId);
            });

        // Bound with validation rather than services.Configure: `required` on KeycloakOptions is a
        // compile-time contract only, and reflective configuration binding ignores it entirely. A
        // missing PublicBaseUrl would otherwise ship a broken link URL into the game with no signal
        // anywhere, so fail at startup instead.
        services.AddOptions<KeycloakOptions>()
            .Bind(configuration.GetSection("Keycloak"))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BaseUrl)
                    && !string.IsNullOrWhiteSpace(o.Realm)
                    && !string.IsNullOrWhiteSpace(o.ProvisioningClientId)
                    && !string.IsNullOrWhiteSpace(o.ProvisioningClientSecret),
                "Keycloak:BaseUrl, Realm, ProvisioningClientId and ProvisioningClientSecret are all required.")
            .Validate(
                o => Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out _),
                "Keycloak:PublicBaseUrl must be an absolute URL reachable from a player's browser.")
            .ValidateOnStart();
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

        // Same per-module TryAdd convention as World/Phone infrastructure: idempotent, so whichever
        // module is registered first wins and a test can still substitute a fake clock.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<IAccountRepository, MartenAccountRepository>();
        services.TryAddScoped<IGameAccountLinkRepository, MartenGameAccountLinkRepository>();
        services.TryAddScoped<IAccountIdentityReconciler, AccountIdentityReconciler>();
        services.TryAddSingleton<ILinkUrlBuilder, LinkUrlBuilder>();
        services.TryAddSingleton<ITokenRevocationStore, InMemoryTokenRevocationStore>();
        services.TryAddScoped<IWhitelistApplicationRepository, MartenWhitelistApplicationRepository>();
        services.TryAddScoped<IGameServerRepository, MartenGameServerRepository>();
        services.TryAddScoped<IHiveSettingsRepository, MartenHiveSettingsRepository>();

        return services;
    }
}
