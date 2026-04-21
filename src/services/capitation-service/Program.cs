using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using CapitationService.Middleware;
using CapitationService.Repositories;
using CapitationService.Services;
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
        Title = "Capitation Service API",
        Version = "v1",
        Description = "Per-member-per-month (PMPM) capitation payments from health plans to providers. " +
                     "Manages capitation contracts, generates monthly payment statements, " +
                     "and disburses payments via NACHA ACH credits or Stripe Connect transfers."
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
        var dbName = sp.GetRequiredService<IConfiguration>()["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });

    builder.Services.AddScoped<ICapitationContractRepository, CapitationContractRepositoryMongo>();
    builder.Services.AddScoped<ICapitationRunRepository, CapitationRunRepositoryMongo>();
    builder.Services.AddScoped<ICapitationStatementRepository, CapitationStatementRepositoryMongo>();
    builder.Services.AddScoped<ICapitationDisbursementRepository, CapitationDisbursementRepositoryMongo>();
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

    builder.Services.AddScoped<ICapitationContractRepository, CapitationContractRepository>();
    builder.Services.AddScoped<ICapitationRunRepository, CapitationRunRepository>();
    builder.Services.AddScoped<ICapitationStatementRepository, CapitationStatementRepository>();
    builder.Services.AddScoped<ICapitationDisbursementRepository, CapitationDisbursementRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Services
builder.Services.AddScoped<ICapitationRunService, CapitationRunService>();
builder.Services.AddSingleton<INachaCreditFileService, NachaCreditFileService>();
builder.Services.AddSingleton<IStripeTransferClient, StripeTransferClient>();
builder.Services.AddScoped<IStripeConnectService, StripeConnectService>();
builder.Services.AddScoped<ICapitationDisbursementService, CapitationDisbursementService>();
builder.Services.AddSingleton<ICapitationEraService, CapitationEraService>();

// Add HttpClients for service-to-service communication
builder.Services.AddHttpClient("CoverageService", client =>
{
    var coverageServiceUrl = builder.Configuration["CoverageService:BaseUrl"] ?? "http://coverage-service:8080";
    client.BaseAddress = new Uri(coverageServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("ProviderService", client =>
{
    var providerServiceUrl = builder.Configuration["ProviderService:BaseUrl"] ?? "http://provider-service:8080";
    client.BaseAddress = new Uri(providerServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("RiskAdjustmentService", client =>
{
    var riskServiceUrl = builder.Configuration["RiskAdjustmentService:BaseUrl"] ?? "http://risk-adjustment-service:8080";
    client.BaseAddress = new Uri(riskServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Capitation Service API v1");
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

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
