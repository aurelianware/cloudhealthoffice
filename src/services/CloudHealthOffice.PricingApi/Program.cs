using System.Text.Json;
using CloudHealthOffice.Infrastructure.Extensions;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CloudHealthOffice.PricingApi.Configuration;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Security;
using CloudHealthOffice.PricingApi.Services;
using CloudHealthOffice.PricingApi.Services.Engine;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using MongoDB.Driver;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    // Secret provider (Azure Key Vault / none)
    builder.Services.AddSecretProvider(builder.Configuration);
    builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

    // ── Serilog ──
    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext());

    // ── Configuration ──
    var pricingOptions = builder.Configuration
        .GetSection(PricingApiOptions.SectionName)
        .Get<PricingApiOptions>() ?? new PricingApiOptions();

    builder.Services.Configure<PricingApiOptions>(
        builder.Configuration.GetSection(PricingApiOptions.SectionName));

    // ── MongoDB ──
    // Connection details come from this service's own options section; feed them to the shared
    // registration so the driver wiring stays in one place.
    builder.Configuration["MongoDb:ConnectionString"] = pricingOptions.MongoConnectionString;
    builder.Configuration["MongoDb:DatabaseName"] = pricingOptions.DatabaseName;
    builder.Services.AddChoDatabase(builder.Configuration);

    // ── Repositories ──
    builder.Services.AddSingleton<IFeeScheduleRepository, MongoFeeScheduleRepository>();
    builder.Services.AddSingleton<IApiKeyRepository, MongoApiKeyRepository>();
    builder.Services.AddSingleton<IUsageRepository, MongoUsageRepository>();

    // ── Services ──
    // Repricing runs on the shared rate resolution engine (ADR 014). Schedules come
    // from this service's own store until the dual-read step wires the canonical one.
    builder.Services.AddScoped<IPricingScheduleSource, LegacyEntryScheduleSource>();
    builder.Services.AddScoped<IRepricingService, RepricingService>();
    builder.Services.AddSingleton<IFeeScheduleLoaderService, FeeScheduleLoaderService>();

    // ── Authentication ──
    // CHO callers (the portal, other services) present a CHO token; the tenant
    // and the actor come from it. Unannotated GETs need a pricing read
    // permission; every unannotated write needs platform:admin, because each
    // write here changes global data (Medicare fee schedules) or the keys of
    // every external customer.
    builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
    {
        auth.DefaultReadPermission = PricingApiAuth.PricePermissions;
        auth.DefaultWritePermission = PricingApiAuth.GlobalWritePermission;
    });

    // External customers hold no CHO token. They keep their API key, now a
    // scheme of its own ("PricingApiKey"), bound to its own credential tenant.
    builder.Services.AddPricingApiCallers();

    // ── Controllers + JSON ──
    // PricingApi publishes camelCase properties + camelCase-cased enum names
    // (e.g. "medicareFeeSchedule") and omits null values. The shared helper is
    // used with camelCaseEnums: true for the string-enum contract; the remaining
    // service-specific overrides are chained in a second AddJsonOptions call.
    builder.Services.AddControllers()
        .AddCloudHealthOfficeJsonOptions(camelCaseEnums: true)
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

    // ── Swagger / OpenAPI ──
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "CloudHealthOffice Claims Pricing API",
            Version = "v1",
            Description = """
                Vendor-neutral claims repricing API by Aurelianware, Inc.
                
                Price professional, outpatient, and inpatient claims against Medicare fee schedules 
                (RBRVS, OPPS, MS-DRG) or upload your own contracted rates.
                
                Free tier: 1,000 claims/month. No credit card required.
                
                **Getting started:**
                1. Browse available fee schedules at GET /api/v1/fee-schedules (no auth needed)
                2. Register for a free API key at https://cloudhealthoffice.com/pricing-api
                3. Look up a code: GET /api/v1/lookup/99213 (CMS Medicare schedules: no auth needed)
                4. Reprice a claim: POST /api/v1/reprice (X-API-Key)

                CHO callers use a CHO bearer token instead of an API key.
                """,
            Contact = new Microsoft.OpenApi.Models.OpenApiContact
            {
                Name = "Aurelianware, Inc.",
                Email = "markus@aurelianware.com",
                Url = new Uri("https://cloudhealthoffice.com")
            },
            License = new Microsoft.OpenApi.Models.OpenApiLicense
            {
                Name = "Business Source License 1.1",
                Url = new Uri("https://github.com/aurelianware/cloudhealthoffice/blob/main/LICENSE")
            }
        });

        c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Name = "X-API-Key",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Description = "API key obtained from https://cloudhealthoffice.com/pricing-api"
        });

        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "CHO access token (CHO callers only)"
        });

        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    // ── Rate Limiting ──
    // Partitioned by the authenticated caller, never by a raw header: an
    // API-key customer by its key id, a CHO caller by tenant and subject, and
    // everyone else (anonymous, or presenting an unknown key) by client
    // address. A made-up key therefore gets no bucket of its own.
    // The pipeline authenticates before the limiter runs (see below).
    builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var rateLimit = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                PricingApiAuth.RateLimitPartition(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimit.PermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = rateLimit.QueueLimit
                });
        });
    });

    // ── CORS ──
    // A public API that partner web apps may call from a browser: an
    // allowlist (Cors:AllowedOrigins, default the portal origin), no
    // credentials, closed when unconfigured outside Development.
    builder.Services.AddChoBrowserCors(builder.Configuration, builder.Environment, policy =>
        policy.WithExposedHeaders("X-RateLimit-Limit", "X-RateLimit-Remaining"));

    // ── Health Checks ──
    builder.Services.AddHealthChecks();

    builder.Services.AddChoObservability(builder.Configuration);

    var app = builder.Build();

    app.UseChoObservability();

    // ── Middleware Pipeline ──
    app.UseSerilogRequestLogging();
    app.UseChoBrowserCors();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "CHO Pricing API v1");
            c.RoutePrefix = "swagger";
        });
    }

    // Authenticate first so the limiter partitions by the verified caller.
    // The result is cached for the request, so the authentication in
    // UseChoAuthentication does not look the key up again.
    app.Use(async (context, next) =>
    {
        var result = await context.AuthenticateAsync();
        if (result.Succeeded && result.Principal is not null)
            context.User = result.Principal;
        await next();
    });
    app.UseRateLimiter();
    app.UseChoAuthentication();
    app.MapControllers();
    app.MapHealthChecks("/health");

    // ── Seed demo data on startup (only if database is empty) ──
    using (var scope = app.Services.CreateScope())
    {
        // Keys stored in plaintext before hashing are hashed here (idempotent).
        await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>().InitializeAsync();

        var loader = scope.ServiceProvider.GetRequiredService<IFeeScheduleLoaderService>();
        if (!await loader.AnySchedulesExistAsync())
        {
            Log.Information("No fee schedules found — seeding demo data...");
            await loader.SeedDemoDataAsync();
        }
        else
        {
            Log.Information("Fee schedules already exist — skipping demo data seeding.");
        }
    }

    Log.Information("CloudHealthOffice Pricing API started on {Urls}", string.Join(", ", app.Urls));
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Expose the generated Program class so WebApplicationFactory can reference it in tests.
public partial class Program { }
