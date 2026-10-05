using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using RfaiService.Repositories;
using RfaiService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── Authentication ───────────────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// that token. RFAI cases point at submitted clinical documentation (PHI), so
// nothing here is anonymous. Callers: fhir-service (CDex Task and
// $submit-attachment, with the CHO caller's token or its own service token for
// SMART callers), authorization-service (A4 requests) and attachment-service
// (275 correlation).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "rfai:read";
    auth.DefaultWritePermission = "rfai:write";
});

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "RFAI Service API",
        Version = "v1",
        Description =
            "Manages Request for Additional Information (RFAI) cases for the " +
            "Availity/Cognizant auth attachment workflow. " +
            "Cases are linked to prior authorizations via the 278 TRN02 auth number."
    });
});

// ── Database ─────────────────────────────────────────────────────────────────

var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IRfaiRepository, RfaiRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    var endpoint = builder.Configuration["CosmosDb:Endpoint"]
        ?? throw new InvalidOperationException("CosmosDb:Endpoint must be configured when MongoDb is not used.");
    var key = builder.Configuration["CosmosDb:Key"]
        ?? throw new InvalidOperationException("CosmosDb:Key must be configured when MongoDb is not used.");

    builder.Services.AddSingleton<CosmosClient>(_ =>
        new CosmosClient(endpoint, key));

    builder.Services.AddScoped<IRfaiRepository, RfaiRepositoryCosmos>();
    Console.WriteLine("Using Cosmos DB database provider");
}

// ── Kafka producer ───────────────────────────────────────────────────────────

var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (!string.IsNullOrEmpty(kafkaBootstrap))
{
    builder.Services.AddSingleton<KafkaProducerService>();
    builder.Services.AddSingleton<IKafkaProducerService>(sp => sp.GetRequiredService<KafkaProducerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<KafkaProducerService>());
}

// ── Domain services ──────────────────────────────────────────────────────────
// The case aggregate's rules live behind one service so every intake path — the
// internal API, the CDex surface in fhir-service, and 275 correlation in
// attachment-service — reaches the same invariants.

builder.Services.AddScoped<IRfaiCaseService, RfaiCaseService>();

// ── Middleware / infra ────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
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

// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RFAI Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

// Token authentication, tenant from the token, then permission policies.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();
