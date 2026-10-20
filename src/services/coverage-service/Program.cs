using CloudHealthOffice.Infrastructure.Extensions;
using CoverageService.Repositories;
using CoverageService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Coverage Service API", 
        Version = "v1",
        Description = "Links Member → Sponsor → Benefit Plan. Populated by X12 834 HD/COB segments. Critical for 270/271 eligibility."
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "coverage:read";
    auth.DefaultWritePermission = "coverage:write";
});

// Database Configuration — MongoDB is the only coverage store (ADR 006);
// the database client is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Coverage Service requires a database. Configure MongoDb:ConnectionString.");
}
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Coverage Service supports only MongoDB. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<ICoverageRepository, CoverageRepositoryMongo>();
builder.Services.AddScoped<IPcpAssignmentRepository, PcpAssignmentRepositoryMongo>();

// PCP assignment + provider client + care team projector
builder.Services.Configure<ProviderServiceOptions>(opts =>
{
    opts.BaseUrl = builder.Configuration["Downstream:ProviderService:BaseUrl"];
});
builder.Services.AddHttpClient<IProviderServiceClient, HttpProviderServiceClient>((sp, client) =>
{
    var baseUrl = builder.Configuration["Downstream:ProviderService:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl)) client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddHttpClient<IPanelCounter, HttpPanelCounter>((sp, client) =>
{
    // Panel counter reads the capitation-style /by-pcp roster. Prefer the
    // capitation-service URL when set; fall back to the coverage-service URL
    // (since coverage itself exposes /by-pcp). If neither is configured, leave
    // BaseAddress null — HttpPanelCounter short-circuits to 0 in that case so
    // panel-limit checks degrade gracefully rather than gating every assignment.
    var baseUrl = builder.Configuration["Downstream:CapitationService:BaseUrl"]
                 ?? builder.Configuration["Downstream:CoverageService:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }
});
builder.Services.AddScoped<IPcpAssignmentService, PcpAssignmentService>();
builder.Services.AddSingleton<ICareTeamProjector, CareTeamProjector>();
// FHIR Coverage projection (ADR 018): the payor named on each Coverage comes
// from Fhir:Coverage:PayorDisplay / PayorReference, else the tenant id.
builder.Services.AddSingleton<IFhirCoverageProjector>(_ => new FhirCoverageProjector(
    builder.Configuration.GetSection(FhirCoverageProjectionOptions.SectionName).Get<FhirCoverageProjectionOptions>()));
builder.Services.AddScoped<ICoverageFhirSearchRepository, CoverageFhirSearchRepository>();
builder.Services.AddScoped<PcpPanelReconciliationJob>();
// Daily: flips coverages whose (future-dated) termination date has arrived to
// Terminated, and promotes Pending ones whose effective date has arrived to Active
// (status hygiene: eligibility and active listings already derive it from the dates).
// CoverageStatusSweep:Enabled=false turns it off.
builder.Services.AddHostedService<CoverageStatusSweepJob>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();
