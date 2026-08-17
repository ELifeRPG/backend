using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class CompanyInfrastructureExtensions
{
    public static IServiceCollection AddCompanyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<ICompaniesStore>(options =>
        {
            options.UseSystemTextJsonWithPrivateSetters();
            options.Connection(configuration.GetConnectionString("CompanyDatabase")!);
            options.Events.DatabaseSchemaName = "companies";
            options.DatabaseSchemaName = "companies";
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Add<CompanyProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<ICompanyRepository, MartenCompanyRepository>();
        services.TryAddScoped<ICompanyRepositoryFactory, MartenCompanyRepositoryFactory>();

        return services;
    }
}
