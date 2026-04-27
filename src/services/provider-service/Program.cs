using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using ProviderService.Adapters;
using ProviderService.HostedServices;
using ProviderService.Middleware;
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
    builder.Services.AddScoped<IProviderTransitionRepository, MongoProviderTransitionRepository>();
    builder.Services.AddScoped<IProviderVersionEventPublisher, MongoProviderVersionEventPublisher>();
    builder.Services.AddHostedService<ProviderVersionEventIndexInitializer>();
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
    builder.Services.AddScoped<IProviderTransitionRepository, CosmosProviderTransitionRepository>();
    // Cosmos-only deployments don't have a provisioned events stream; the
    // Noop publisher logs a warning so ops can spot the missing wiring
    // without breaking the lifecycle path.
    builder.Services.AddScoped<IProviderVersionEventPublisher, NoopProviderVersionEventPublisher>();
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
