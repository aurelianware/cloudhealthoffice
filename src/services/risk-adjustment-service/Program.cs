using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;
using RiskAdjustmentService;
using RiskAdjustmentService.Middleware;
using RiskAdjustmentService.Repositories;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
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
        Title = "Risk Adjustment Service API",
        Version = "v1",
        Description = "Healthcare risk adjustment scoring for Cloud Health Office. " +
                     "Provides per-member HCC risk scores, measurement-year data, " +
                     "and population risk analytics for Medicare Advantage, Medicaid, and ACA plans."
    });
    c.UseInlineDefinitionsForEnums();
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
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

    builder.Services.AddScoped<IRiskScoreRepository, RiskScoreRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"];
        var key = config["CosmosDb:Key"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");
        }

        var serializerOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(serializerOptions)
        };
        return new CosmosClient(endpoint, key, options);
    });

    builder.Services.AddScoped<IRiskScoreRepository, RiskScoreRepository>();
}

// HCC Risk Adjustment Engine
builder.Services.AddSingleton<IIcdToHccMapper, IcdToHccMapper>();
builder.Services.AddSingleton<IHccHierarchyResolver, HccHierarchyResolver>();
builder.Services.AddSingleton<IRiskScoreCalculator, RiskScoreCalculator>();
builder.Services.AddSingleton<CloudHealthOffice.RiskAdjustmentEngine.Services.RiskAdjustmentEngine>();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Risk Adjustment Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

// Multi-tenant middleware
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
