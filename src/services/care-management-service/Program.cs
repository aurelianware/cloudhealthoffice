using CareManagementService.HostedServices;
using CareManagementService.Repositories;
using CareManagementService.Services.Work;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — the same bootstrap shape as
// every other service in the platform.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Care Management Service API",
        Version = "v1",
        Description =
            "Care management cases, assessments, care plans and activities, with a read-only " +
            "FHIR CarePlan / Goal / CareTeam projection.",
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting care manager
// come from that token only, never from a request body.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "care:read";
    auth.DefaultWritePermission = "care:write";
});

// ── Database ─────────────────────────────────────────────────────────
// MongoDB is the only care-management store (ADR 006, ADR 017). A host with
// no database configured refuses to start, in every environment, rather than
// going Ready and failing every request.
if (builder.Services.AddChoDatabase(builder.Configuration) is null)
{
    throw new InvalidOperationException(
        "Care Management Service requires a database. Configure MongoDb:ConnectionString.");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CareCaseIndexes>();
builder.Services.AddScoped<ICareCaseRepository, CareCaseRepositoryMongo>();
builder.Services.AddHostedService<CareCaseIndexInitializer>();

// ── Work queue ───────────────────────────────────────────────────────
// Care activities are pushed into work-management-service, which already
// routes, assigns and ages work for the rest of the platform. Without it
// configured, activities are still recorded on the case — they are simply in
// nobody's queue, and nothing about the care record depends on the queue.
var workManagementUrl = builder.Configuration["Services:WorkManagementServiceUrl"];
if (!string.IsNullOrWhiteSpace(workManagementUrl))
{
    builder.Services.AddHttpClient(HttpCareWorkItemGateway.HttpClientName, client =>
    {
        client.BaseAddress = new Uri(workManagementUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddScoped<ICareWorkItemGateway, HttpCareWorkItemGateway>();
}
else
{
    builder.Services.AddSingleton<ICareWorkItemGateway, NullCareWorkItemGateway>();
}

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
