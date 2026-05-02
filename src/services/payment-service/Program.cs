using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using PaymentService.Middleware;
using PaymentService.Repositories;
using PaymentService.Services;
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
        Title = "Payment Service API",
        Version = "v1",
        Description = "835 ERA (Electronic Remittance Advice) payment processing for Cloud Health Office. " +
                     "Handles payment posting, reconciliation, and claim remittance tracking."
    });
});

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB when MongoDb:ConnectionString is present, Cosmos DB otherwise
if (!string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
        new MongoClient(sp.GetRequiredService<IConfiguration>()["MongoDb:ConnectionString"]));

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var dbName = sp.GetRequiredService<IConfiguration>()["MongoDb:DatabaseName"] ?? "PaymentDB";
        return client.GetDatabase(dbName);
    });

    builder.Services.AddScoped<IPaymentRepository, PaymentRepositoryMongo>();
    builder.Services.AddScoped<IPaymentRunRepository, PaymentRunRepositoryMongo>();
    builder.Services.AddScoped<IReversalRunRepository, ReversalRunRepositoryMongo>();
    builder.Services.AddScoped<IEraEnvelopeRepository, EraEnvelopeRepositoryMongo>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"];
        var key = config["CosmosDb:Key"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");

        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        });
    });

    builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
    builder.Services.AddScoped<IPaymentRunRepository, PaymentRunRepository>();
    builder.Services.AddScoped<IReversalRunRepository, ReversalRunRepository>();
    // EraEnvelope persistence on Cosmos-only deployments uses the
    // in-memory fallback. payment-service's canonical store is Mongo;
    // Cosmos paths are dev-only and don't need durable EraEnvelope storage.
    builder.Services.AddSingleton<IEraEnvelopeRepository, InMemoryEraEnvelopeRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Services
builder.Services.AddScoped<IPaymentRunService, PaymentRunService>();
builder.Services.AddScoped<IReversalRunService, ReversalRunService>();
builder.Services.AddScoped<IEraGeneratorService, EraGeneratorService>();

// 5.10 — batched 835 generation. Stateless services, Singleton DI.
builder.Services.AddSingleton<IBatchEraGeneratorService, BatchEraGeneratorService>();
builder.Services.AddSingleton<ICarcRarcMappingService, CarcRarcMappingService>();
builder.Services.AddScoped<ITradingPartnersClient, TradingPartnersClient>();

// Add HttpClient for claims service integration
builder.Services.AddHttpClient("ClaimsService", client =>
{
    var claimsServiceUrl = builder.Configuration["ClaimsService:BaseUrl"] ?? "http://claims-service:8080";
    client.BaseAddress = new Uri(claimsServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// 5.10 — typed HttpClient for trading-partner-service NPI lookups
// during PaymentRun execution. 10s timeout matches credentialing/
// resolution-client conventions; trading-partner-service is a
// low-frequency dependency so a slow lookup shouldn't fail an entire
// PaymentRun outright.
builder.Services.AddHttpClient(TradingPartnersClient.HttpClientName, client =>
{
    var tradingPartnerUrl = builder.Configuration["TradingPartnerService:BaseUrl"]
        ?? "http://trading-partner-service:8080";
    client.BaseAddress = new Uri(tradingPartnerUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Health checks (MongoDB or Cosmos DB, claims-service HTTP)
var claimsServiceHealthUrl = builder.Configuration["ClaimsService:BaseUrl"] ?? "http://claims-service:8080";
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
    options.HttpDependencies["claims-service"] = $"{claimsServiceHealthUrl.TrimEnd('/')}/health/live";
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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment Service API v1");
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

// Enable WebApplicationFactory<Program> access from test projects
public partial class Program { }
