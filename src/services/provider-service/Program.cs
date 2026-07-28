using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using ProviderService.Adapters;
using ProviderService.HostedServices;
using ProviderService.Middleware;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Provider Service API",
        Version = "v1",
        Description = "Provider directory and network participation management for Cloud Health Office. " +
                     "Validates provider NPI, checks network status, retrieves contracted rates for claims adjudication."
    });
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    // MongoDB Registration
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(sp => 
    {
        return new MongoDB.Driver.MongoClient(mongoConnectionString);
    });
    
    builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var databaseName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(databaseName);
    });

    builder.Services.AddScoped<IProviderRepository, ProviderRepositoryMongo>();
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepositoryMongo>();
    builder.Services.AddScoped<IProviderTransitionRepository, MongoProviderTransitionRepository>();
    builder.Services.AddScoped<IProviderVersionEventPublisher, MongoProviderVersionEventPublisher>();
    builder.Services.AddScoped<IProviderVerificationEventPublisher, MongoProviderVerificationEventPublisher>();
    builder.Services.AddScoped<INetworkParticipationEventPublisher, MongoNetworkParticipationEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventPublisher, MongoCredentialingEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventRepository, MongoCredentialingEventRepository>();
    builder.Services.AddHostedService<ProviderQueryIndexInitializer>();
    builder.Services.AddHostedService<ProviderVersionEventIndexInitializer>();
    builder.Services.AddHostedService<ProviderVerificationEventIndexInitializer>();
    builder.Services.AddHostedService<NetworkParticipationEventIndexInitializer>();
    builder.Services.AddHostedService<CredentialingEventIndexInitializer>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    // Cosmos DB client (singleton)
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"];
        var key = config["CosmosDb:Key"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");
        }

        return new CosmosClient(endpoint, key);
    });

    // Repositories
    builder.Services.AddScoped<IProviderRepository, ProviderRepository>();
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
    builder.Services.AddScoped<IProviderTransitionRepository, CosmosProviderTransitionRepository>();
    // Cosmos-only deployments don't have a provisioned events stream; the
    // Noop publisher logs a warning so ops can spot the missing wiring
    // without breaking the lifecycle path.
    builder.Services.AddScoped<IProviderVersionEventPublisher, NoopProviderVersionEventPublisher>();
    builder.Services.AddScoped<IProviderVerificationEventPublisher, NoopProviderVerificationEventPublisher>();
    builder.Services.AddScoped<INetworkParticipationEventPublisher, NoopNetworkParticipationEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventPublisher, NoopCredentialingEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventRepository, CosmosCredentialingEventRepository>();
}

// Provider versioning service (5.1 — provider identity & versioning)
builder.Services.AddScoped<IProviderVersioningService, ProviderVersioningService>();

// MPIP rate service (FL SMMC 3.0 physician incentive program)
builder.Services.AddScoped<IMpipRateService, MpipRateService>();

// Provider adapter pattern (5.2 — tenant-routed provider directory backends).
// Cache is singleton (TTL across requests); adapters and factory are scoped
// because the CHO adapter wraps scoped repository services. Tenant-service
// HTTP client uses a 5-second timeout so a flaky tenant-service can't stall
// provider reads — the cache falls back to "cho" on any failure.
builder.Services.AddHttpClient(ProviderTenantConfigCache.HttpClientName)
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<ProviderTenantConfigCache>();
builder.Services.AddScoped<IProviderAdapter, ChoProviderAdapter>();
builder.Services.AddScoped<IProviderAdapter, QnxtProviderAdapter>();
builder.Services.AddScoped<IProviderAdapter, FacetsProviderAdapter>();
builder.Services.AddScoped<IProviderAdapter, HealthEdgeProviderAdapter>();
builder.Services.AddScoped<ProviderAdapterFactory>();

// Organization (Network) services + adapters (5.3 — network as first-class
// organization). Reuses ProviderTenantConfigCache because the Network
// entity lives in provider-service and reads the same tenant config block.
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<IOrganizationAdapter, ChoOrganizationAdapter>();
builder.Services.AddScoped<IOrganizationAdapter, QnxtOrganizationAdapter>();
builder.Services.AddScoped<IOrganizationAdapter, FacetsOrganizationAdapter>();
builder.Services.AddScoped<OrganizationAdapterFactory>();

// Network roster (5.4 — paginated, filterable provider roster scoped to
// a single Organization). Reads cached IntegrityScore directly from the
// Provider row; never invokes ProviderVerificationOrchestrator on the
// read path.
builder.Services.AddScoped<INetworkRosterService, NetworkRosterService>();

// Verification write-back (5.4.5 — projection from provider-verification-service
// onto Provider.IntegrityScore + IntegrityRating + LastVerifiedAt + NextVerificationDue).
// HTTP — not project reference — preserves the service boundary and avoids
// duplicating the engine's six data-source clients into provider-service.
// IntegrityProjectionWorker iterates per-tenant on a schedule (default 1h);
// IntegrityProjectionAdminController surfaces a one-shot backfill endpoint.
builder.Services.Configure<IntegrityProjectionOptions>(
    builder.Configuration.GetSection(IntegrityProjectionOptions.SectionName));
builder.Services.AddHttpClient<IProviderVerificationClient, HttpProviderVerificationClient>(client =>
{
    var baseUrl = builder.Configuration["ProviderVerification:BaseUrl"]
        ?? "http://provider-verification-service";
    client.BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("ProviderVerification:TimeoutSeconds", 30));
})
.SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<IProviderIntegrityProjectionService, ProviderIntegrityProjectionService>();
// Capability 5.10 — per-tenant staleness telemetry that piggybacks on the
// worker sweep. No new hosted service; the reporter is a scoped helper
// invoked from inside IntegrityProjectionWorker's per-tenant loop.
builder.Services.AddScoped<IIntegrityProjectionStalenessReporter, IntegrityProjectionStalenessReporter>();
builder.Services.AddHostedService<IntegrityProjectionWorker>();

// Network-participation panel-gating backfill (5.5 — one-shot
// admin-triggered patch of legacy participations to legacy-unconstrained
// defaults; pairs with controller-side soft-validation telemetry that
// drives the eventual hard-validation cutover).
builder.Services.Configure<NetworkParticipationBackfillOptions>(
    builder.Configuration.GetSection(NetworkParticipationBackfillOptions.SectionName));
builder.Services.AddScoped<IPanelGatingValidator, PanelGatingValidator>();
builder.Services.AddScoped<INetworkParticipationBackfillService, NetworkParticipationBackfillService>();

// Credentialing workflow (5.6 — event-sourced credentialing chain
// projected onto Provider.CredentialingStatus / CredentialingDate /
// RecredentialingDueDate via the bypass write path mirroring 5.4.5 and
// 5.5). The projector is a pure function — singleton-safe.
builder.Services.AddSingleton<CredentialingProjector>();
builder.Services.AddScoped<ICredentialingService, CredentialingService>();

// FHIR R4 Practitioner projection (5.7 — provider-service is the
// canonical source for the Practitioner FHIR resource; fhir-service
// proxies /fhir/r4/Practitioner/* to FhirPractitionerController). The
// projector is stateless (singleton-safe) and mirrors member-service's
// IFhirPatientProjector.
builder.Services.AddSingleton<IFhirPractitionerProjector, FhirPractitionerProjector>();

// FHIR R4 PractitionerRole projection (5.8 — provider-service is the
// canonical source for the PractitionerRole FHIR resource; fhir-service
// proxies /fhir/r4/PractitionerRole/* to FhirPractitionerRoleController).
// One PractitionerRole projects per Provider.NetworkParticipation with a
// non-null NetworkId. The projector is stateless (singleton-safe) and
// mirrors the 5.7 IFhirPractitionerProjector pattern.
builder.Services.AddSingleton<IFhirPractitionerRoleProjector, FhirPractitionerRoleProjector>();

// FHIR R4 Organization projection (5.9 — provider-service is the
// canonical source for the Organization FHIR resource; fhir-service
// proxies /fhir/r4/Organization/* to FhirOrganizationController). Two
// source entities (Organization network entity → type=ins; Provider with
// ProviderType=Organization → type=prov) project into a single FHIR
// Organization resource type. The projector is stateless (singleton-safe)
// and mirrors the 5.7 / 5.8 projector pattern.
builder.Services.AddSingleton<IFhirOrganizationProjector, FhirOrganizationProjector>();

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

// CORS (for development)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Provider Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

// Multi-tenant middleware (extract TenantId from JWT or headers)
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
