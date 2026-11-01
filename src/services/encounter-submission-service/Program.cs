using Microsoft.OpenApi.Models;
using EncounterSubmissionService.KafkaConsumers;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.StateRoutes;
using EncounterSubmissionService.Workers;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Middleware;
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
        Title = "Encounter Submission Service API",
        Version = "v1",
        Description = "Tracks state Medicaid encounter submission: Florida (FMMIS), and Texas (TMHP) " +
                     "and California (DHCS) 837 routes once configured. Batches adjudicated claims, " +
                     "processes 999 acknowledgments, and fires deadline warning events."
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "encounters:read";
    auth.DefaultWritePermission = "encounters:write";
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

// HTTP context accessor (the Kafka consumer sets a per-message context)
builder.Services.AddHttpContextAccessor();

// Inter-service HTTP clients (named client pattern). AddChoAuthentication puts
// ChoOutboundTokenHandler on every factory client: it forwards the caller's token,
// or mints a service token for the X-Tenant-ID the request names.
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

builder.Services.AddHttpClient("MemberService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:MemberService"]
        ?? "http://member-service:8080");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient("ProviderService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderService"]
        ?? "http://provider-service:8080");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// Business services
builder.Services.AddScoped<IEncounterSubmissionService, EncounterSubmissionServiceImpl>();

// State encounter routes. EncounterRoutes:{STATE} configures each route; Texas and
// California stay off until a route is enabled and a plan's state-issued identities
// are configured under EncounterRoutes:{STATE}:Tenants:{tenantId}.
builder.Services.Configure<Dictionary<string, StateEncounterRouteOptions>>(
    builder.Configuration.GetSection("EncounterRoutes"));
builder.Services.AddSingleton<IStateEncounterRouteRegistry>(sp => new StateEncounterRouteRegistry(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dictionary<string, StateEncounterRouteOptions>>>()));
builder.Services.AddScoped<IEncounterControlNumberSource, MongoEncounterControlNumberSource>();
builder.Services.AddScoped<IStateEncounterBatchService, StateEncounterBatchService>();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Encounter Submission Service API v1");
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

public partial class Program { }
