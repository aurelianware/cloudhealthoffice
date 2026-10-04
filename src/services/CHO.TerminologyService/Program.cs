using CHO.TerminologyService.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CHO.TerminologyService.Data;
using CHO.TerminologyService.Services;
using CHO.TerminologyService.Services.CodeSystemCatalog;
using CHO.TerminologyService.Services.Loaders;
using CHO.TerminologyService.Services.Rules;
using MongoDB.Driver;
using Serilog;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// One-off migration (operator CLI): dotnet CHO.TerminologyService.dll --backfill-override-tenants
// [--dry-run]. Sets the tenant on override map versions saved before versions carried one.
if (args.Contains(CHO.TerminologyService.Migrations.BackfillOverrideVersionTenants.Switch))
{
    Environment.ExitCode = await CHO.TerminologyService.Migrations.BackfillOverrideVersionTenants.RunAsync(
        args, builder.Configuration);
    return;
}

// ──────────────────────────────────────────────────────
// Logging
// ──────────────────────────────────────────────────────
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "CHO.TerminologyService")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} | {Message:lj}{NewLine}{Exception}"));

// ──────────────────────────────────────────────────────
// Configuration
// ──────────────────────────────────────────────────────
var terminologyOptions = builder.Configuration
    .GetSection(TerminologyServiceOptions.SectionName)
    .Get<TerminologyServiceOptions>() ?? new TerminologyServiceOptions();

builder.Services.Configure<TerminologyServiceOptions>(
    builder.Configuration.GetSection(TerminologyServiceOptions.SectionName));

// ──────────────────────────────────────────────────────
// MongoDB
// ──────────────────────────────────────────────────────
// Connection details come from this service's own options section; feed them to the shared
// registration so the driver wiring stays in one place.
builder.Configuration["MongoDb:ConnectionString"] = terminologyOptions.MongoConnectionString;
builder.Configuration["MongoDb:DatabaseName"] = terminologyOptions.MongoDatabaseName;
builder.Services.AddChoDatabase(builder.Configuration);

// ──────────────────────────────────────────────────────
// Services
// ──────────────────────────────────────────────────────
builder.Services.AddSingleton<IConceptMapRepository, MongoConceptMapRepository>();
builder.Services.AddSingleton<ICodeSystemCatalogRepository, MongoCodeSystemCatalogRepository>();
builder.Services.AddSingleton<IContextRuleEngine, ContextRuleEngine>();
builder.Services.AddSingleton<ITerminologyTranslationService, TerminologyTranslationService>();
builder.Services.AddHostedService<CodeSystemCatalogSeedService>();

// Map loaders (register all implementations)
builder.Services.AddSingleton<IMapLoader, Rf2MapLoader>();
builder.Services.AddSingleton<IMapLoader, CsvMapLoader>();

// Caching
builder.Services.AddMemoryCache();

// Background syndication (startup auto-load + scheduled update checks)
builder.Services.AddMapSyndication();

// ──────────────────────────────────────────────────────
// ASP.NET Core
// ──────────────────────────────────────────────────────
// NOTE: CHO.TerminologyService intentionally does NOT call
// AddCloudHealthOfficeJsonOptions(). This is a FHIR ConceptMap/$translate
// service whose wire format follows FHIR conventions (numeric enum coding,
// CamelCase + WhenWritingNull) rather than the platform default.
// See docs/architecture/shared-json-options.md.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

// ──────────────────────────────────────────────────────
// Authentication
// ──────────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
// Reads (including the POST $translate/$batch-translate operations, which are annotated)
// need terminology:read; service tokens satisfy it. Tenant-scoped writes (a tenant's own
// plan overrides) need settings:manage. Loading a global map that every tenant reads
// needs platform:admin, checked in the load action.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "terminology:read";
    auth.DefaultWritePermission = "settings:manage";
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "CHO Terminology Service",
        Version = "v1",
        Description = "FHIR ConceptMap/$translate terminology crosswalk service. " +
                      "Part of Cloud Health Office — vendor-neutral healthcare payer infrastructure.",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "Aurelianware",
            Email = "markus@aurelianware.com",
            Url = new Uri("https://cloudhealthoffice.com")
        }
    });
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// ──────────────────────────────────────────────────────
// Pipeline
// ──────────────────────────────────────────────────────
app.UseSerilogRequestLogging();

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Map auto-load on startup and scheduled update checks are handled by
// MapSyndicationService (registered above via AddMapSyndication()).
// It runs as a BackgroundService: loads mounted files on startup,
// then checks NLM for new editions daily.

Log.Information("CHO Terminology Service started on {Urls}", string.Join(", ", app.Urls));
app.Run();
