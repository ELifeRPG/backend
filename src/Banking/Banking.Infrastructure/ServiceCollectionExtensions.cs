using System.Reflection;
using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
using ELifeRPG.Shared.Integration.Abstractions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class BankingInfrastructureExtensions
{
    public static IServiceCollection AddBankingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var store = services.AddMartenStore<IBankingStore>(options =>
        {
            options.UseSystemTextJsonWithPrivateSetters();
            options.Connection(configuration.GetConnectionString("BankingDatabase")!);
            options.Events.DatabaseSchemaName = "banking";
            options.DatabaseSchemaName = "banking";
            // Marten 9's default (UseIdentityMapForAggregates = true) opts any session that calls
            // Events.FetchForWriting<T> into identity-map tracking for T, for that session's whole
            // lifetime — including later, unrelated LoadAsync<T> calls made through the very same
            // scoped IBankAccountRepository (e.g. a query handler running later in the same request
            // scope, after a FetchForUpdateAsync call elsewhere in that scope). Since
            // MartenBankAccountRepository holds one session per request, that would make FindByIdAsync
            // silently return a stale, session-cached document instead of the current row once
            // FetchForUpdateAsync had run once in that scope. Restoring V8 (non-caching) semantics here
            // keeps every load a real, fresh query — see MartenBankAccountRepository.FetchForUpdateAsync
            // for the matching aggregate-instance-reuse gotcha this also avoids on the write side.
            options.Events.UseIdentityMapForAggregates = false;

            options.Projections.Add<BankProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);
            options.Projections.Add<BankAccountProjection>(JasperFx.Events.Projections.ProjectionLifecycle.Inline);

            // The code is embedded in every account number, and the number is how a player addresses
            // an account — both must be unique, enforced by the database rather than a pre-check that
            // two concurrent writers could both pass. The number embeds the bank code, so global
            // uniqueness is the same as uniqueness within the bank.
            options.Schema.For<Bank>().UniqueIndex(x => x.Code);
            options.Schema.For<BankAccount>().UniqueIndex(x => x.Number);
        });

        // Seeding runs as a hosted service on host start. The build boots the host too, to generate
        // openapi/eliferpg-api-v1.json, and must not need a database to do that (CI has none).
        if (Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
        {
            store.InitializeWith(new BankSeed());
        }

        services.TryAddScoped<IBankRepository, MartenBankRepository>();
        services.TryAddScoped<IBankAccountRepository, MartenBankAccountRepository>();
        services.TryAddScoped<ITransactionParticipant<IBankAccountRepository>, MartenBankAccountParticipant>();

        return services;
    }
}
