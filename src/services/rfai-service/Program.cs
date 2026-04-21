using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using RfaiService.Middleware;
using RfaiService.Repositories;
using RfaiService.Services;
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

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(
        _ => new MongoDB.Driver.MongoClient(mongoConnectionString));

    builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var dbName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });

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

// ── Middleware / infra ────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

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

app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();
