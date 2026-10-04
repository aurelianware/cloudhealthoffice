using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;
using RiskAdjustmentService;
using RiskAdjustmentService.Repositories;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
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
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
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

// HTTP context accessor (the repositories read the request tenant from it)
builder.Services.AddHttpContextAccessor();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it (ICurrentActor / HttpContext.Items["TenantId"]), never from a header,
// query string or body. Risk scores carry diagnoses (PHI): reads need
// risk-adjustment:read (Finance; ComplianceOfficer through *:read). Writes
// (score upserts, RAF calculations, submission status, deletes) need
// risk-adjustment:write only (Finance, which calculates the scores and runs the
// submissions, holds it; finance:write does not reach risk scores). Each action
// also names its permission.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = RiskAdjustmentPermissions.Read;
    auth.DefaultWritePermission = RiskAdjustmentPermissions.Write;
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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Risk Adjustment Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
