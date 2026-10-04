using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using ProviderService.Adapters;
using ProviderService.HostedServices;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.FieldProtection;
using ProviderService.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// One-off migration (operator CLI): dotnet provider-service.dll --encrypt-bank-accounts
// [--tenant <id>] [--dry-run]. Re-encrypts bank numbers stored before encryption.
if (args.Contains(ProviderService.Migrations.EncryptProviderBankAccounts.Switch))
{
    Environment.ExitCode = await ProviderService.Migrations.EncryptProviderBankAccounts.RunAsync(
        args, builder.Configuration, builder.Environment);
    return;
}

builder.Services.AddControllers(options => options.Filters.Add<FieldProtectionExceptionFilter>())
    .AddCloudHealthOfficeJsonOptions()
    // Responses never carry full bank-account, routing or tax numbers.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new ProviderService.Security.MaskedProviderBankAccountJsonConverter()));
builder.Services.AddEndpointsApiExplorer();

// ── Encryption at rest ─────────────────────────────────────────────
// Bank routing, account and tax numbers (ProviderBankAccounts records and the
// legacy Provider.BankAccount copy on provider documents and version rows) are
// stored encrypted (ASP.NET Data Protection; key ring in Azure Blob Storage
// wrapped by a Key Vault key, shared by every pod; a local key ring in
// Development/Testing). Without FieldProtection:KeyRing elsewhere, writes of
// those numbers fail (503) instead of storing plaintext.
// See docs/security/bank-account-data.md.
var keyRing = builder.Services.AddChoFieldProtection(
    builder.Configuration, builder.Environment, ProviderBankAccountProtection.Purpose);
Console.WriteLine($"Field protection key ring: {keyRing}");
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

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// that token. Credentialing writes need providers:credential, network
// endpoints networks:*, contracted rates contracts:read (annotated on the
// controllers).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "providers:read";
    auth.DefaultWritePermission = "providers:write";
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    // MongoDB Registration
    
    builder.Services.AddScoped<ProviderRepositoryMongo>();
    builder.Services.AddScoped<IProviderRepository>(sp => new ProtectedProviderRepository(
        sp.GetRequiredService<ProviderRepositoryMongo>(),
        sp.GetRequiredService<IFieldProtector>(),
        sp.GetRequiredService<ILogger<ProtectedProviderRepository>>()));
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepositoryMongo>();
    builder.Services.AddScoped<IProviderTransitionRepository, MongoProviderTransitionRepository>();
    builder.Services.AddScoped<IProviderVersionEventPublisher, MongoProviderVersionEventPublisher>();
    builder.Services.AddScoped<IProviderVerificationEventPublisher, MongoProviderVerificationEventPublisher>();
    builder.Services.AddScoped<INetworkParticipationEventPublisher, MongoNetworkParticipationEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventPublisher, MongoCredentialingEventPublisher>();
    builder.Services.AddScoped<ICredentialingEventRepository, MongoCredentialingEventRepository>();
    builder.Services.AddScoped<MongoProviderBankAccountRepository>();
    builder.Services.AddScoped<IProviderBankAccountRepository>(sp => new ProtectedProviderBankAccountRepository(
        sp.GetRequiredService<MongoProviderBankAccountRepository>(),
        sp.GetRequiredService<IFieldProtector>(),
        sp.GetRequiredService<ILogger<ProtectedProviderBankAccountRepository>>()));
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
    builder.Services.AddScoped<ProviderRepository>();
    builder.Services.AddScoped<IProviderRepository>(sp => new ProtectedProviderRepository(
        sp.GetRequiredService<ProviderRepository>(),
        sp.GetRequiredService<IFieldProtector>(),
        sp.GetRequiredService<ILogger<ProtectedProviderRepository>>()));
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
    // Needs a "ProviderBankAccounts" container (partition key /tenantId).
    builder.Services.AddScoped<CosmosProviderBankAccountRepository>();
    builder.Services.AddScoped<IProviderBankAccountRepository>(sp => new ProtectedProviderBankAccountRepository(
        sp.GetRequiredService<CosmosProviderBankAccountRepository>(),
        sp.GetRequiredService<IFieldProtector>(),
        sp.GetRequiredService<ILogger<ProtectedProviderBankAccountRepository>>()));
}

// Provider versioning service (5.1 — provider identity & versioning)
builder.Services.AddScoped<IProviderVersioningService, ProviderVersioningService>();
// Bank-account dual control: a change is pending until a second user with
// payments:approve approves it; payments read only the approved account.
builder.Services.AddScoped<IProviderBankAccountChangeService, ProviderBankAccountChangeService>();

// MPIP rate service (FL SMMC 3.0 physician incentive program)
builder.Services.AddScoped<IMpipRateService, MpipRateService>();

// Provider adapter pattern (5.2 — tenant-routed provider directory backends).
// Cache is singleton (TTL across requests); adapters and factory are scoped
// because the CHO adapter wraps scoped repository services. Tenant-service
// HTTP client uses a 5-second timeout so a flaky tenant-service can't stall
// provider reads — the cache falls back to "cho" when tenant-service is
// unreachable, but a 401/403 from it is an error, never the default.
// AddChoAuthentication puts ChoOutboundTokenHandler on every factory client:
// it forwards the caller's token, or mints a service token for the
// X-Tenant-ID the request names (the cache and the verification client both
// set it).
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

// HTTP context accessor (repositories read the token tenant from the request)
builder.Services.AddHttpContextAccessor();

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

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


// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
