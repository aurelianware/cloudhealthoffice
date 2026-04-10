using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using ClaimsService.EDI.Florida;
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
}

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

var app = builder.Build();

// Shared middleware pipeline: exception handling, Swagger (dev), tenant middleware, CORS, health checks
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
