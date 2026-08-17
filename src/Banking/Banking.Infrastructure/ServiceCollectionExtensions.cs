using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class BankingInfrastructureExtensions
{
    public static IServiceCollection AddBankingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<IBankingStore>(options =>
        {
            options.UseSystemTextJsonWithPrivateSetters();
            options.Connection(configuration.GetConnectionString("BankingDatabase")!);
            options.Events.DatabaseSchemaName = "banking";
            options.DatabaseSchemaName = "banking";
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Add<BankProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
            options.Projections.Add<BankAccountProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        services.TryAddScoped<IBankRepository, MartenBankRepository>();
        services.TryAddScoped<IBankAccountRepository, MartenBankAccountRepository>();
        services.TryAddScoped<IBankAccountRepositoryFactory, MartenBankAccountRepositoryFactory>();

        return services;
    }
}
