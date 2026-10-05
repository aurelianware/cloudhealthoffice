using CloudHealthOffice.Infrastructure.Extensions;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from it.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "accumulators:read";
    auth.DefaultWritePermission = "accumulators:write";
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - Accumulator Service API",
        Version = "v1",
        Description = "Member plan-year accumulators (deductible / OOP / per-service). Snapshots driven by ClaimFinalized events and manual adjustments."
    });
});

// ── Database ─────────────────────────────────────────────────────────
// Mirrors eligibility-service's auto-detect pattern: Mongo if configured, else Cosmos.
var mongoConnection = builder.Configuration["MongoDb:ConnectionString"];
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);
if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();
    builder.Services.AddScoped<IProcessedClaimStore, ProcessedClaimStoreMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    var cosmosConnection = builder.Configuration["CosmosDb:ConnectionString"]
        ?? throw new InvalidOperationException("Database connection not configured: set MongoDb:ConnectionString or CosmosDb:ConnectionString");

    builder.Services.AddSingleton(_ => new CosmosClient(cosmosConnection, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    }));
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryCosmos>();
    builder.Services.AddScoped<IProcessedClaimStore, ProcessedClaimStoreCosmos>();
}

// ── Domain service ───────────────────────────────────────────────────
builder.Services.AddScoped<IAccumulatorService, AccumulatorService.Services.AccumulatorService>();

// ── Kafka publisher + consumer ───────────────────────────────────────
// Publisher registered as singleton IHostedService so StartAsync builds the
// producer during app startup. Graceful degrade to no-op if Kafka is unavailable.
builder.Services.AddSingleton<KafkaAccumulatorEventPublisher>();
builder.Services.AddSingleton<IAccumulatorEventPublisher>(sp => sp.GetRequiredService<KafkaAccumulatorEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<KafkaAccumulatorEventPublisher>());
builder.Services.AddHostedService<ClaimFinalizedConsumer>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Accumulator Service API v1"));
}

app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
