using System.Text.Json;
using Microsoft.Azure.Cosmos;
using EligibilityService;
using EligibilityService.Adapters;
using EligibilityService.Middleware;
using EligibilityService.Repositories;
using EligibilityService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Add services
builder.Services.AddControllers(options =>
{
    options.Filters.Add<TenantActionFilter>();
}).AddCloudHealthOfficeJsonOptions();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - Eligibility Service API",
        Version = "v1",
        Description = "Real-time eligibility verification (270/271 EDI transactions)"
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

    builder.Services.AddScoped<IEligibilityRepository, EligibilityRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    // Cosmos DB
    var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"] 
        ?? throw new InvalidOperationException("Cosmos DB connection string not configured");

    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(jsonOptions)
        };
        return new CosmosClient(cosmosConnectionString, options);
    });

    builder.Services.AddScoped<IEligibilityRepository, EligibilityRepository>();
}

// HTTP Client for service calls (shared by adapters, factory, and eligibility service)
builder.Services.AddHttpClient("EligibilityDefault")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

// Eligibility adapters — each tenant can be configured to use a different platform.
// Registered as singletons with IHttpClientFactory injected (not HttpClient) so that
// handler rotation and DNS refresh work correctly over the application lifetime.
builder.Services.AddSingleton<IEligibilityAdapter, ChoEligibilityAdapter>();
builder.Services.AddSingleton<IEligibilityAdapter, AvailityEligibilityAdapter>();
builder.Services.AddSingleton<IEligibilityAdapter, ChangeHealthcareEligibilityAdapter>();
builder.Services.AddSingleton<EligibilityAdapterFactory>();

builder.Services.AddHttpClient<IEligibilityService, EligibilityServiceImpl>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

builder.Services.AddScoped<IEligibilityService, EligibilityServiceImpl>();

// 270/271 EDI services
builder.Services.AddScoped<IEdi270Parser, Edi270Parser>();
builder.Services.AddScoped<IEdi271Generator, Edi271Generator>();

// Temporal eligibility (date-bound read projection over coverage-service)
builder.Services.AddSingleton<IAccumulatorClient, StubAccumulatorClient>();
builder.Services.AddScoped<ITemporalEligibilityService, TemporalEligibilityService>();

// Shared messaging bus. Backend (ServiceBus / InMemory / Null) is resolved
// from Messaging:* config + environment by AddChoMessaging.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// Batch eligibility storage (in-memory for dev, Cosmos+Blob+IMessageBus for
// production). Resolution logic lives in
// BatchEligibilityServiceCollectionExtensions.
builder.Services.AddBatchEligibilityStorage(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IBatchEligibilityService, BatchEligibilityService>();
builder.Services.AddHostedService<BatchEligibilityQueueWorker>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Health checks (MongoDB or Cosmos DB)
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

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Eligibility Service API v1");
    });
}

app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
