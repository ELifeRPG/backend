using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain;
using ELifeRPG.Characters.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class CharacterInfrastructureExtensions
{
    public static IServiceCollection AddCharacterInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<ICharactersStore>(options =>
        {
            options.UseSystemTextJsonWithPrivateSetters();
            options.Connection(configuration.GetConnectionString("CharacterDatabase")!);
            options.Events.DatabaseSchemaName = "characters";
            options.DatabaseSchemaName = "characters";
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Add<CharacterProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<ICharacterRepository, MartenCharacterRepository>();

        return services;
    }
}
