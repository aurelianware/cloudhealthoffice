using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using ReferenceDataService.Repositories;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

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

// Add repositories
builder.Services.AddScoped<IReferenceDataRepository, ReferenceDataRepository>();

// Cosmos DB Client — hosts the ComplianceConfig container
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? builder.Configuration["CosmosDb:Endpoint"];
var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY")
    ?? builder.Configuration["CosmosDb:Key"];

if (!string.IsNullOrEmpty(cosmosEndpoint) && !string.IsNullOrEmpty(cosmosKey))
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
else
{
    builder.Services.AddSingleton<IComplianceConfigRepository, InMemoryComplianceConfigRepository>();
}

// Azure AD Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
    },
    options => { builder.Configuration.Bind("AzureAd", options); });

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
        policy.RequireRole("Administrator"));
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

// Authentication MUST come before authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
