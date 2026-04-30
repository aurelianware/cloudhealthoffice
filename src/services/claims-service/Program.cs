using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Observability;
using ClaimsService.EDI.Florida;
using ClaimsService.Fhir;
using ClaimsService.HostedServices;
using ClaimsService.Repositories;
using ClaimsService.Services;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Shared infrastructure: health checks, CORS, Swagger, database, tenant middleware
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Service";
    options.ServiceDescription = "Healthcare claims processing for Cloud Health Office. " +
                                 "Handles 837 claim submission, 835 remittance, 277 status updates, and adjudication results.";
});

// Repository — Cosmos or Mongo based on config (database client registered by shared lib)
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddScoped<IClaimRepository, ClaimRepositoryMongo>();
    builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryMongo>();

    // Claim version event publisher (5.1) — Mongo append-only stream is the
    // system-of-record for the version chain. Mirrors
    // MongoProviderVersionEventPublisher / MongoPlanVersionEventPublisher.
    builder.Services.AddScoped<IClaimVersionEventPublisher, MongoClaimVersionEventPublisher>();
    builder.Services.AddHostedService<ClaimVersionEventIndexInitializer>();
}
else
{
    var cosmosEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    var cosmosKey = builder.Configuration["CosmosDb:Key"];
    var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"];

    if (string.IsNullOrEmpty(cosmosConnectionString) &&
        (string.IsNullOrEmpty(cosmosEndpoint) || string.IsNullOrEmpty(cosmosKey)))
    {
        throw new InvalidOperationException(
            "Claims Service requires a database. Configure either MongoDb:ConnectionString " +
            "or CosmosDb:ConnectionString (or CosmosDb:Endpoint + CosmosDb:Key).");
    }

    builder.Services.AddScoped<IClaimRepository, ClaimRepository>();
    builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryCosmos>();

    // Cosmos-only deployments don't have a provisioned events stream; the
    // Noop publisher logs a warning so ops can spot the missing wiring
    // without breaking the lifecycle path.
    builder.Services.AddScoped<IClaimVersionEventPublisher, NoopClaimVersionEventPublisher>();
}

// FHIR R4 ExplanationOfBenefit projector — hand-built JsonObject to avoid
// the Hl7.Fhir.R4 transitive dep; used by the v1 member-scoped claims endpoint.
builder.Services.AddSingleton<IExplanationOfBenefitProjector, ExplanationOfBenefitProjector>();

// 277CA acknowledgment generator
builder.Services.AddScoped<IClaimAcknowledgmentService, ClaimAcknowledgmentService>();

// Inter-service HTTP clients
builder.Services.AddHttpClient("ProviderService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderService"]
        ?? "http://provider-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient("ReferenceDataService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ReferenceDataService"]
        ?? "http://reference-data-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// FL FMMIS encounter submission pipeline
builder.Services.AddScoped<IProviderService, HttpProviderService>();
builder.Services.AddScoped<ITenantComplianceConfigService, HttpTenantComplianceConfigService>();
builder.Services.AddScoped<FmmisClaimTransformer>();
builder.Services.AddScoped<FmmisFileBuilder>();

// FL SMMC 3.0 MPIP rate enhancement
builder.Services.AddScoped<IMpipRateClient, MpipRateClient>();
builder.Services.AddScoped<IMpipAdjudicationEnhancer, MpipAdjudicationEnhancer>();

// Claim lifecycle event publisher (Kafka). Singleton + IHostedService so the
// underlying producer is initialized at app start and cleanly shut down.
// Always registered: when Kafka:BootstrapServers is unset the publisher runs
// in degraded mode and logs a warning, so dev/test environments without Kafka
// remain functional.
builder.Services.AddSingleton<ClaimEventPublisher>();
builder.Services.AddSingleton<IClaimEventPublisher>(sp => sp.GetRequiredService<ClaimEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClaimEventPublisher>());

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Shared middleware pipeline: exception handling, Swagger (dev), tenant middleware, CORS, health checks
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
