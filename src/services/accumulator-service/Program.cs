using AccumulatorService.Middleware;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<TenantActionFilter>();
}).AddCloudHealthOfficeJsonOptions();

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
if (!string.IsNullOrWhiteSpace(mongoConnection))
{
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnection));
    builder.Services.AddScoped(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var dbName = builder.Configuration["MongoDb:DatabaseName"] ?? "AccumulatorDB";
        return client.GetDatabase(dbName);
    });
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

builder.Services.AddCors(o => o.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

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

app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
