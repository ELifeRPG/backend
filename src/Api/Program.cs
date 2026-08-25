using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Api.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters.ValidateAudience = false;
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var jti = context.Principal?.FindFirst("jti")?.Value;
                if (jti is not null)
                {
                    var revocationStore = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationStore>();
                    if (revocationStore.IsRevoked(jti))
                    {
                        context.Fail("Token has been revoked.");
                    }
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization();

// The single, central Mediator dispatcher for every module — see ARCHITECTURE.md §9e. Add each
// new module's Application assembly marker here as it's built.
builder.Services.AddMediator(options =>
{
    options.Assemblies =
    [
        typeof(ELifeRPG.Accounts.Application.AssemblyMarker),
        typeof(ELifeRPG.Characters.Application.AssemblyMarker),
        typeof(ELifeRPG.Banking.Application.AssemblyMarker),
        typeof(ELifeRPG.Companies.Application.AssemblyMarker),
        typeof(ELifeRPG.Items.Application.AssemblyMarker),
        typeof(ELifeRPG.Shops.Application.AssemblyMarker),
    ];
    // Handlers depend on Marten's scoped IDocumentSession; Mediator's default handler lifetime is
    // Singleton, which WebApplicationBuilder.Build() correctly rejects as a captive-dependency error.
    options.ServiceLifetime = Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient;
});
builder.Services.AddSingleton(typeof(Mediator.IPipelineBehavior<,>), typeof(RequestMetricsBehaviour<,>));

builder.Services.AddAccountModule(builder.Configuration);
builder.Services.AddWhitelistModule(builder.Configuration);
builder.Services.AddGameServerModule(builder.Configuration);
builder.Services.AddHiveModule(builder.Configuration);
builder.Services.AddCharacterModule(builder.Configuration);
builder.Services.AddBankingModule(builder.Configuration);
builder.Services.AddCompanyModule(builder.Configuration);
builder.Services.AddItemModule(builder.Configuration);
builder.Services.AddShopModule(builder.Configuration);
builder.Services.AddCrossModuleIntegration(builder.Configuration);

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing => tracing
        .AddSource(Activities.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(builder.Configuration.GetConnectionString("Tracing")!)))
    .WithMetrics(metrics => metrics
        .AddMeter(Metrics.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(builder.Configuration.GetConnectionString("Tracing")!)));

builder.Services.AddOpenApi("v1");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs", options =>
    {
        options.WithDynamicBaseServerUrl();
        options.AddDocuments("v1");
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAccountModule();
app.MapWhitelistModule();
app.MapGameServerModule();
app.MapHiveModule();
app.MapCharacterModule();
app.MapBankingModule();
app.MapCompanyModule();
app.MapItemModule();
app.MapShopModule();

app.Run();
