using Microsoft.Azure.Cosmos;
using CloudHealthOffice.TradingPartnerService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { 
        Title = "Trading Partner Service API", 
        Version = "v1",
        Description = "Manages trading partner configurations, SFTP paths, and X12 settings for multi-tenant EDI processing"
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
// Reads need trading-partners:read (ClaimsSupervisor, Finance; service tokens satisfy it).
// No trading-partners:write permission exists, so writes need settings:manage.
// The payment-service NPI lookup also admits payments:read (see the controller).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "trading-partners:read";
    auth.DefaultWritePermission = "settings:manage";
});

// Database provider selection. MongoDB is the default so the service stays cloud-agnostic;
// Cosmos DB's native SDK is opt-in via Database:Provider=CosmosDb.
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.CosmosDb)
{
    // Cosmos DB Client (singleton)
    builder.Services.AddSingleton(sp =>
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
            ?? builder.Configuration["CosmosDb:Endpoint"]
            ?? throw new InvalidOperationException("COSMOS_ENDPOINT not configured");

        var key = Environment.GetEnvironmentVariable("COSMOS_KEY")
            ?? builder.Configuration["CosmosDb:Key"]
            ?? throw new InvalidOperationException("COSMOS_KEY not configured");

        return new CosmosClient(endpoint, key);
    });
}

// Repository and services
if (databaseProvider == ChoDatabaseProvider.CosmosDb)
{
    builder.Services.AddScoped<ITradingPartnerRepository, TradingPartnerRepository>();
}
else
{
    builder.Services.AddScoped<ITradingPartnerRepository, TradingPartnerRepositoryMongo>();
}
builder.Services.AddScoped<PathResolver>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

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

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// Health checks before auth so they're accessible without a token
app.MapChoHealthChecks();

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();
