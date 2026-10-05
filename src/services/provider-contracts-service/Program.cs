using Microsoft.OpenApi.Models;
using CloudHealthOffice.Infrastructure.Extensions;
using MongoDB.Driver;
using ProviderContractsService.Repositories;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it, never from a header, the query string or the body. Reads need
// contracts:read, writes contracts:write (ProviderRelations holds both;
// Finance reads).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "contracts:read";
    auth.DefaultWritePermission = "contracts:write";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Provider Contracts Service API",
        Version = "v1",
        Description = "Master provider contract management. Holds the legal agreement between " +
                     "the health plan and a provider/group. Payment-method-specific configuration " +
                     "(capitation rates, FFS fee schedules) are child records referencing contracts by ContractId."
    });
});

// HTTP context accessor (the repository reads the token's tenant and actor)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IProviderContractRepository, MongoProviderContractRepository>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    throw new InvalidOperationException("MongoDb:ConnectionString must be configured for provider-contracts-service");
}

// Health checks
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Provider Contracts Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
