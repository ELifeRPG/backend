using ELifeRPG.Phone.Api;
using ELifeRPG.Phone.Domain.Apps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    // Named to the realm's own convention (infra/keycloak/eliferpg-realm.json): what a gameserver's
    // Bridge holds is gameserver:<module>:<verb>, what staff hold is a bare <x>:<verb>, matching
    // accounts:manage and inventory:manage. These were bare phone:* until the SIM merge, but were
    // never registered in the realm at all, so no token could carry them and nothing broke by
    // aligning them.
    public const string PhoneReadScope = "gameserver:phone:read";
    public const string PhoneWriteScope = "gameserver:phone:write";
    public const string PhoneProvisionScope = "gameserver:phone:provision";
    public const string PhoneManageScope = "phone:manage";

    /// <summary>
    /// Suspend/restore sits behind its own scope so an in-game Police/State faction can be granted
    /// exactly this later without also gaining the moderation powers of phone:manage.
    /// </summary>
    public const string PhoneEnforceScope = "phone:enforce";

    private const string ReadPolicy = "Phone.Read";
    private const string WritePolicy = "Phone.Write";
    private const string ProvisionPolicy = "Phone.Provision";
    private const string ManagePolicy = "Phone.Manage";
    private const string EnforcePolicy = "Phone.Enforce";

    public static IServiceCollection AddPhoneModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPhoneInfrastructure(configuration);
        services.AddSignalR();
        services.AddSingleton<PhoneHubNotifier>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneReadScope)))
            .AddPolicy(WritePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneWriteScope)))
            .AddPolicy(ProvisionPolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneProvisionScope)))
            .AddPolicy(ManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneManageScope)))
            .AddPolicy(EnforcePolicy, policy => policy.RequireAssertion(context => HasScope(context, PhoneEnforceScope)));

        return services;
    }

    public static WebApplication MapPhoneModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Phone");

        app.MapHub<PhoneHub>("hubs/phone").RequireAuthorization(WritePolicy);

        MapDevices(group);
        MapEnforcement(group);
        MapContacts(group);
        MapMessages(group);
        MapAdmin(group);

        return app;
    }

    private static bool HasScope(AuthorizationHandlerContext context, string scope) =>
        context.User.FindFirst("scope")?.Value.Split(' ').Contains(scope) == true;

    private static bool TryParseApp(string raw, out AppKey key, out IResult? problem)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out key) && AppCatalog.Contains(key))
        {
            problem = null;
            return true;
        }

        problem = Results.Problem(
            title: $"appKey must be one of: {string.Join(", ", AppCatalog.Entries.Keys)}",
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }
}
