using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CloudHealthOffice.PricingApi.Configuration;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Middleware;
using CloudHealthOffice.PricingApi.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
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
    builder.Services.AddSingleton<IMongoClient>(
        new MongoClient(pricingOptions.MongoConnectionString));

    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IMongoClient>().GetDatabase(pricingOptions.DatabaseName));

    // ── Repositories ──
    builder.Services.AddSingleton<IFeeScheduleRepository, MongoFeeScheduleRepository>();
    builder.Services.AddSingleton<IApiKeyRepository, MongoApiKeyRepository>();
    builder.Services.AddSingleton<IUsageRepository, MongoUsageRepository>();

    // ── Services ──
    builder.Services.AddScoped<IRepricingService, RepricingService>();
    builder.Services.AddSingleton<IFeeScheduleLoaderService, FeeScheduleLoaderService>();

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
                3. Look up a code: GET /api/v1/lookup/99213
                4. Reprice a claim: POST /api/v1/reprice
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
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Request.Headers["X-API-Key"].ToString() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 10
                }));
    });

    // ── CORS (allow Swagger UI and partner integrations) ──
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Default", policy =>
        {
            policy.AllowAnyOrigin()  // Tighten in production
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("X-RateLimit-Limit", "X-RateLimit-Remaining");
        });
    });

    // ── Health Checks ──
    builder.Services.AddHealthChecks();

    builder.Services.AddChoObservability(builder.Configuration);

    var app = builder.Build();

    app.UseChoObservability();

    // ── Middleware Pipeline ──
    app.UseSerilogRequestLogging();
    app.UseCors("Default");

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "CHO Pricing API v1");
            c.RoutePrefix = "swagger";
        });
    }

    app.UseRateLimiter();
    app.UseApiKeyAuthentication();
    app.MapControllers();
    app.MapHealthChecks("/health");

    // ── Seed demo data on startup (only if database is empty) ──
    using (var scope = app.Services.CreateScope())
    {
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
