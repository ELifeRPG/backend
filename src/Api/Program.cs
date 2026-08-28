using System.Globalization;
using System.Threading.RateLimiting;
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Api.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
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
        typeof(ELifeRPG.Phone.Application.AssemblyMarker),
        typeof(ELifeRPG.World.Application.AssemblyMarker),
    ];
    // Handlers depend on Marten's scoped IDocumentSession; Mediator's default handler lifetime is
    // Singleton, which WebApplicationBuilder.Build() correctly rejects as a captive-dependency error.
    options.ServiceLifetime = Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient;
});
builder.Services.AddSingleton(typeof(Mediator.IPipelineBehavior<,>), typeof(RequestMetricsBehaviour<,>));

// Host-level rate limiting. Individual modules register their own named policies (the same way they
// register their own authorization policies — see WorldModule.AddWorldModule for the first two) and
// opt endpoints in with RequireRateLimiting; nothing is limited by default. What lives here is the
// shared shape of a rejection, because a 429 is the one problem response no endpoint handler ever
// gets to write: the rate limiter short-circuits the pipeline before the handler runs, so the
// module-level `retryable` helpers cannot reach it.
//
// Both halves of that shape matter to a store-and-forward client. `retryable: true` is what separates
// "hold this batch and send it again" from every non-retryable rejection on the same endpoints, which
// must be dropped rather than replayed forever; `Retry-After` is what stops the retry being a guess.
// Getting either wrong turns a transient limit into either a lost batch or a hot loop — see
// docs/bridge.md, which states buffering as a REQUIREMENT on the Bridge and this pair as what the
// backend guarantees in return, and ARCHITECTURE.md §5.1.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        // A token bucket always leases a RetryAfter on failure; the guard is for a future policy on
        // some other limiter type that does not, where omitting the header beats emitting a wrong one.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(NumberFormatInfo.InvariantInfo);
        }

        // Deliberately the same ProblemDetails shape every other rejection on these endpoints uses, so
        // a client parses one body format across the whole write path rather than special-casing 429.
        await Results.Problem(
                title: "rate_limited: too many requests from this client — wait for the interval in the Retry-After header and resend unmodified",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?> { ["retryable"] = true })
            .ExecuteAsync(context.HttpContext);
    };
});

builder.Services.AddAccountModule(builder.Configuration);
builder.Services.AddWhitelistModule(builder.Configuration);
builder.Services.AddGameServerModule(builder.Configuration);
builder.Services.AddHiveModule(builder.Configuration);
builder.Services.AddCharacterModule(builder.Configuration);
builder.Services.AddBankingModule(builder.Configuration);
builder.Services.AddCompanyModule(builder.Configuration);
builder.Services.AddItemModule(builder.Configuration);
builder.Services.AddShopModule(builder.Configuration);
builder.Services.AddPhoneModule(builder.Configuration);
builder.Services.AddWorldModule(builder.Configuration);
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

// The published OpenAPI document is the source of truth for every Kiota-generated client
// (ARCHITECTURE.md §3.3), so anything a client must be able to read has to survive the generator. Two
// things did not, and both are fixed here rather than by hand-editing openapi/eliferpg-api-v1.json,
// which is regenerated on every build.
builder.Services.AddOpenApi("v1", options =>
{
    // (1) `retryable`. Every problem document the World module returns carries it, docs/bridge.md makes
    // it the flag the entire Bridge retry contract turns on — and the generated ProblemDetails schema
    // declared no `additionalProperties`, so a generator is entitled to drop every extension member on
    // the floor. Declaring `retryable` explicitly gives a client a typed accessor for the one member
    // that is load-bearing; leaving additional properties open keeps the others (`lastAppliedSequence`
    // on `stale_sequence`, and anything added later) reachable rather than silently discarded.
    //
    // Applied to the shared ProblemDetails schema rather than per-endpoint because that is the only
    // schema the document has for a problem: it is one component referenced by every ProducesProblem in
    // every module. `retryable` is therefore optional here (absent on other modules' problems), which
    // is exactly what docs/bridge.md already tells a client — treat an absent `retryable` as `false`.
    options.AddSchemaTransformer((schema, context, _) =>
    {
        if (context.JsonTypeInfo.Type == typeof(ProblemDetails))
        {
            // An empty schema, not just AdditionalPropertiesAllowed = true: that flag is the OpenAPI
            // default and so is not written out, leaving the document exactly as silent about
            // extensions as it was before. `additionalProperties: { }` is the form a generator
            // actually keys on to emit a catch-all bag.
            schema.AdditionalProperties = new OpenApiSchema();
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Properties["retryable"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Boolean | JsonSchemaType.Null,
                Description =
                    "Whether resending this exact request unmodified can succeed. Present on every "
                    + "problem the World module returns; absent elsewhere, and an absent value means "
                    + "false. See docs/bridge.md.",
            };
        }

        return Task.CompletedTask;
    });

    // (2) `Retry-After` on 429. The rate limiter always sets it (see OnRejected above) and a
    // store-and-forward client is told to wait exactly that long before resending — but a response
    // header is not inferred from anything in the endpoint's metadata, so no ProducesProblem call could
    // have declared it. Applied to every 429 in the document rather than to the two World endpoints by
    // name: the header is a property of the host's single OnRejected handler, so any endpoint that ever
    // gains a rate-limit policy gets the same guarantee and the same declaration.
    options.AddOperationTransformer((operation, _, _) =>
    {
        if (operation.Responses?.TryGetValue("429", out var response) is true && response is OpenApiResponse concrete)
        {
            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concrete.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds to wait before resending this request unmodified.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            };
        }

        return Task.CompletedTask;
    });
});

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

// After authentication: gameserver limits partition on the client_id claim, which does not exist
// until the token has been validated.
app.UseRateLimiter();

app.MapAccountModule();
app.MapWhitelistModule();
app.MapGameServerModule();
app.MapHiveModule();
app.MapCharacterModule();
app.MapBankingModule();
app.MapCompanyModule();
app.MapItemModule();
app.MapShopModule();
app.MapPhoneModule();
app.MapWorldModule();

app.Run();
