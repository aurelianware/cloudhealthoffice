using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Repositories;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using ReferenceDataService.Repositories.Canonical;
using ReferenceDataService.Migrations;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Resolve PostgreSQL connection string (supports env var substitution)
var postgresConnection = builder.Configuration.GetConnectionString("PostgreSQL") ?? string.Empty;
var postgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
if (!string.IsNullOrEmpty(postgresPassword))
{
    postgresConnection = postgresConnection.Replace("${POSTGRES_PASSWORD}", postgresPassword);
}

if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException("PostgreSQL connection string is not configured.");
}

// Add PostgreSQL DbContext
builder.Services.AddDbContext<ReferenceDataContext>(options =>
    options.UseNpgsql(postgresConnection));
builder.Services.AddScoped<ReferenceDataSchemaMigrator>();

// Add repositories
builder.Services.AddScoped<IReferenceDataRepository, ReferenceDataRepository>();
builder.Services.AddScoped<CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository, CanonicalReferenceDataRepository>();

// Cosmos DB Client — hosts the ComplianceConfig container
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? builder.Configuration["CosmosDb:Endpoint"];
var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY")
    ?? builder.Configuration["CosmosDb:Key"];

// MongoDB is preferred so the service stays cloud-agnostic; Cosmos DB's native SDK is opt-in
// via Database:Provider=CosmosDb. With neither configured the in-memory repository is used,
// which does not survive a restart and is only suitable for local runs and tests.
var useCosmosCompliance =
    string.Equals(builder.Configuration["Database:Provider"], "CosmosDb", StringComparison.OrdinalIgnoreCase)
    && !string.IsNullOrEmpty(cosmosEndpoint)
    && !string.IsNullOrEmpty(cosmosKey);

var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (useCosmosCompliance)
{
    builder.Services.AddSingleton(sp =>
    {
        var options = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };
        return new CosmosClient(cosmosEndpoint, cosmosKey, options);
    });
    builder.Services.AddSingleton<IComplianceConfigRepository, CosmosComplianceConfigRepository>();
}
else if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddChoDatabase(builder.Configuration);
    // Scoped, not singleton: IMongoDatabase is resolved per request so tenant-scoped database
    // names stay correct.
    builder.Services.AddScoped<IComplianceConfigRepository, MongoComplianceConfigRepository>();
}
else
{
    builder.Services.AddSingleton<IComplianceConfigRepository, InMemoryComplianceConfigRepository>();
}

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
// Reads need reference-data:read (service tokens satisfy it); writes to tenant-scoped data
// (compliance config, a tenant's own canonical codes) need settings:manage. Writing global
// code sets shared by every tenant needs platform:admin, checked in the import action.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "reference-data:read";
    auth.DefaultWritePermission = "settings:manage";
});

// Add memory cache for hot code lookups
builder.Services.AddMemoryCache();

// Add controllers
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks (PostgreSQL — uses NpgSql check alongside standard CHO checks)
builder.Services.AddChoHealthChecks()
    .AddNpgSql(postgresConnection, name: "postgres", tags: new[] { "ready", "db" }, timeout: TimeSpan.FromSeconds(10));

// Add CORS
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

// The schema migration needs PostgreSQL. In-process pipeline tests, which substitute the
// repositories, turn it off with ReferenceData:ApplySchemaMigrationsOnStartup=false.
if (builder.Configuration.GetValue("ReferenceData:ApplySchemaMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ReferenceDataSchemaMigrator>().ApplyAsync();
}

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
