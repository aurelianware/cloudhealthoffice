using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
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

// Database — MongoDB is the only risk-score store (ADR 006); the client is
// registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Risk Adjustment Service requires a database. Configure MongoDb:ConnectionString.");
}

if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Risk Adjustment Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<IRiskScoreRepository, RiskScoreRepositoryMongo>();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Risk Adjustment Service API v1");
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
