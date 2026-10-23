using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using WorkManagementService.HostedServices;
using WorkManagementService.Repositories;
using WorkManagementService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── Authentication ───────────────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it. Reads default to workqueue:read and writes to workqueue:work; supervisor
// and admin actions name workqueue:assign / workqueue:admin explicitly.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = ChoRolePermissions.WorkQueueRead;
    auth.DefaultWritePermission = ChoRolePermissions.WorkQueueWork;
});

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Work Management Service API",
        Version = "v1",
        Description =
            "One work engine for every kind of operational work (claims pends first): routing on work-item " +
            "attributes, entitlement by role, supervisor-managed queue memberships, Get Next with leases, " +
            "and supervisor push.",
    });
});

// ── Database ─────────────────────────────────────────────────────────────────
// MongoDB is the only persistent work store (ADR 006).
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);
if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IWorkStore>(sp => new MongoWorkStore(sp.GetRequiredService<IMongoDatabase>()));
    builder.Services.AddScoped<IWorkspaceStore>(sp => new MongoWorkspaceStore(sp.GetRequiredService<IMongoDatabase>()));
    Console.WriteLine("[work-management-service] Using MongoDB database provider");
}
else if (databaseProvider is not null)
{
    throw new InvalidOperationException(
        "work-management-service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}
else
{
    // Nothing configured: only a Development / Testing host may run on the
    // process-local store (state is lost on restart and not shared by replicas).
    if (!ChoTestingEnvironment.AllowsDevelopmentSecrets(builder.Environment))
        throw new InvalidOperationException(
            "work-management-service needs a database. Configure MongoDb:ConnectionString.");
    builder.Services.AddSingleton<IWorkStore, InMemoryWorkStore>();
    builder.Services.AddSingleton<IWorkspaceStore, InMemoryWorkspaceStore>();
    Console.WriteLine("[work-management-service] No database configured: using the in-memory store (Development only)");
}

// ── Work engine ──────────────────────────────────────────────────────────────
builder.Services.Configure<WorkManagementOptions>(builder.Configuration.GetSection(WorkManagementOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WorkSeedTracker>();
builder.Services.AddScoped<WorkConfigService>();
builder.Services.AddScoped<WorkIntakeService>();
builder.Services.AddScoped<WorkDistributionService>();

// claims-service: the reconcile source. The shared outbound handler forwards
// the caller's token inside a request and mints this service's own token
// (for the tenant in X-Tenant-ID) from the background reconcile.
builder.Services.AddHttpClient(HttpClaimsWorkSource.HttpClientName, client =>
{
    var baseUrl = builder.Configuration["Services:ClaimsService"] ?? "http://claims-service.cloudhealthoffice";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<IClaimsWorkSource, HttpClaimsWorkSource>();

// enrollment-import-service: enrollment exceptions become Enrollment834 work
// through the periodic reconcile (no event stream yet). Same token handling.
builder.Services.AddHttpClient(HttpEnrollmentWorkSource.HttpClientName, client =>
{
    var baseUrl = builder.Configuration["Services:EnrollmentImportService"] ?? "http://enrollment-import-service.cloudhealthoffice";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<IEnrollmentWorkSource, HttpEnrollmentWorkSource>();

// tenant-service: whether a user other than the caller holds a permission,
// for supervisor push and queue membership. Cached, and refused rather than
// guessed when it cannot be read; see WorkEntitlementDirectory.
builder.Services.AddHttpClient(WorkEntitlementDirectory.HttpClientName, client =>
{
    var baseUrl = builder.Configuration["TenantService:BaseUrl"]
                  ?? builder.Configuration["Services:TenantService"]
                  ?? "http://tenant-service.cloudhealthoffice";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IWorkEntitlementDirectory, WorkEntitlementDirectory>();

// claims.work.v1 consumer (disabled without Kafka) and the periodic
// reconcile / re-score (reconcile on automatically when there is no Kafka).
builder.Services.AddHostedService<ClaimWorkEventConsumer>();
builder.Services.AddHostedService<WorkMaintenanceService>();

// ── Middleware / infra ───────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});
// No CORS: called server-to-server only (the portal is Blazor Server).

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Work Management Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

// Token authentication, tenant from the token, then permission policies.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
