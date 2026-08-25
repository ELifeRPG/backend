using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
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
            // Marten 9's default (UseIdentityMapForAggregates = true) opts any session that calls
            // Events.FetchForWriting<T> into identity-map tracking for T, for that session's whole
            // lifetime — including later, unrelated LoadAsync<T> calls made through the very same
            // scoped ICompanyRepository (e.g. a query handler running later in the same request scope,
            // after a FetchForUpdateAsync call elsewhere in that scope). Since MartenCompanyRepository
            // holds one session per request, that would make FindByIdAsync silently return a stale,
            // session-cached document instead of the current row once FetchForUpdateAsync had run once
            // in that scope. Restoring V8 (non-caching) semantics here keeps every load a real, fresh
            // query — see MartenCompanyRepository.FetchForUpdateAsync for the matching
            // aggregate-instance-reuse gotcha this also avoids on the write side. Same fix as Banking's
            // (Task 1 of this plan).
            options.Events.UseIdentityMapForAggregates = false;

            options.Projections.Add<CompanyProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<ICompanyRepository, MartenCompanyRepository>();
        services.TryAddScoped<ICompanyRepositoryFactory, MartenCompanyRepositoryFactory>();

        return services;
    }
}
