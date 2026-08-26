using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Phone.Infrastructure.Common;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class PhoneInfrastructureExtensions
{
    public static IServiceCollection AddPhoneInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMartenStore<IPhoneStore>(options =>
        {
            options.Connection(configuration.GetConnectionString("PhoneDatabase")!);
            options.Events.DatabaseSchemaName = "phone";
            options.DatabaseSchemaName = "phone";

            options.Projections.Add<PhoneModelProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<PhoneDeviceProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<SimCardProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<ContactBookProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<MessageThreadProjection>(ProjectionLifecycle.Inline);

            // The number is the routing key for every send, and two SIMs sharing one would make
            // delivery ambiguous — so uniqueness is enforced by the database, not by a pre-check the
            // generator could race.
            options.Schema.For<SimCard>().UniqueIndex(x => x.NumberValue);

            // A thread is looked up on exactly this pair on the hot path of every send.
            options.Schema.For<MessageThread>().Index(x => x.ThreadKey);
        });

        // Injected rather than called statically so the rate-limit window is testable without waiting
        // a real minute.
        services.TryAddSingleton(TimeProvider.System);

        // Shared unit of work for the whole scope — see PhoneSession for why this module needs one.
        services.TryAddScoped<IPhoneSession, PhoneSession>();

        services.TryAddScoped<IPhoneModelRepository, MartenPhoneModelRepository>();
        services.TryAddScoped<IPhoneDeviceRepository, MartenPhoneDeviceRepository>();
        services.TryAddScoped<ISimCardRepository, MartenSimCardRepository>();
        services.TryAddScoped<IContactBookRepository, MartenContactBookRepository>();
        services.TryAddScoped<IMessageThreadRepository, MartenMessageThreadRepository>();
        services.TryAddScoped<ISimSendWindowRepository, MartenSimSendWindowRepository>();
        services.TryAddScoped<IPhoneModerationRepository, MartenPhoneModerationRepository>();

        return services;
    }
}
