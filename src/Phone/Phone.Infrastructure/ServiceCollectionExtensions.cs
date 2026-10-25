using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Notifications;
using ELifeRPG.Phone.Infrastructure.Common;
using ELifeRPG.Shared.Infrastructure;
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
            options.UseSystemTextJsonWithPrivateSetters(new PhoneNumberJsonConverter());

            options.Connection(configuration.GetConnectionString("PhoneDatabase")!);
            options.Events.DatabaseSchemaName = "phone";
            options.DatabaseSchemaName = "phone";

            options.Projections.Add<PhoneDeviceProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<ContactBookProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<MessageThreadProjection>(ProjectionLifecycle.Inline);
            options.Projections.Add<PhoneCallProjection>(ProjectionLifecycle.Inline);

            // The number is the routing key for every send, and two phones sharing one would make
            // delivery ambiguous — so uniqueness is enforced by the database, not by a pre-check the
            // generator could race.
            options.Schema.For<PhoneDevice>().UniqueIndex(x => x.NumberValue);

            // A thread is looked up on exactly this pair on the hot path of every send.
            options.Schema.For<MessageThread>().Index(x => x.ThreadKey);

            // Unlike PendingDelivery (read once, on power-on), this table is read by every phone on
            // every poll — it cannot afford to be unindexed.
            options.Schema.For<PhoneNotification>().Index(x => x.PhoneIdValue);

            // The ring poll is the hottest read in the module — a phone hits it every couple of
            // seconds while its dialer is open, and it filters on the callee alone.
            options.Schema.For<PhoneCall>().Index(x => x.CalleePhoneIdValue);
        });

        // Injected rather than called statically so the rate-limit window is testable without waiting
        // a real minute.
        services.TryAddSingleton(TimeProvider.System);

        // Shared unit of work for the whole scope — see PhoneSession for why this module needs one.
        services.TryAddScoped<IPhoneSession, PhoneSession>();

        services.TryAddScoped<IPhoneDeviceRepository, MartenPhoneDeviceRepository>();
        services.TryAddScoped<IPhonePreferencesRepository, MartenPhonePreferencesRepository>();
        services.TryAddScoped<IContactBookRepository, MartenContactBookRepository>();
        services.TryAddScoped<IMessageThreadRepository, MartenMessageThreadRepository>();
        services.TryAddScoped<IPhoneNotificationRepository, MartenPhoneNotificationRepository>();
        services.TryAddScoped<IPhoneCallRepository, MartenPhoneCallRepository>();
        services.TryAddScoped<IPhoneSendWindowRepository, MartenPhoneSendWindowRepository>();
        services.TryAddScoped<IPhoneModerationRepository, MartenPhoneModerationRepository>();

        return services;
    }
}
