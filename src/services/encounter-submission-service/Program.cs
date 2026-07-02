using Microsoft.OpenApi.Models;
using EncounterSubmissionService.KafkaConsumers;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.Workers;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Middleware;
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
        Title = "Encounter Submission Service API",
        Version = "v1",
        Description = "Tracks the 60-day AHCA encounter submission window for FL Medicaid claims. " +
                     "Batches adjudicated claims for FMMIS submission, processes 999 acknowledgments, " +
                     "and fires deadline warning events."
    });
});

// Database Configuration (MongoDB required — EncounterSubmissionServiceImpl depends on IMongoDatabase)
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"]
    ?? throw new InvalidOperationException(
        "Encounter Submission Service requires MongoDB. Configure MongoDb:ConnectionString.");

builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(sp =>
    new MongoDB.Driver.MongoClient(mongoConnectionString));

builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
    var databaseName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
    return client.GetDatabase(databaseName);
});

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(new TenantMiddlewareOptions());

// Inter-service HTTP clients (named client pattern)
builder.Services.AddHttpClient("ClaimsService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ClaimsService"]
        ?? "http://claims-service:8080");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient("ReferenceDataService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ReferenceDataService"]
        ?? "http://reference-data-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// Business services
builder.Services.AddScoped<IEncounterSubmissionService, EncounterSubmissionServiceImpl>();

// Kafka consumer for adjudication-completed events
var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (!string.IsNullOrEmpty(kafkaBootstrap))
{
    builder.Services.AddHostedService<AdjudicationCompletedConsumer>();
}

// Background worker for deadline monitoring and batching (every 4 hours)
builder.Services.AddHostedService<EncounterSubmissionWorker>();

// Health checks (MongoDB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Encounter Submission Service API v1");
        c.RoutePrefix = string.Empty;
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

public partial class Program { }
