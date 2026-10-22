using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using EncounterService;
using EncounterService.Fhir;
using EncounterService.Repositories;
using EncounterService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Audit;

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
        Title = "Encounter Service API",
        Version = "v1",
        Description = "Healthcare encounter submission lifecycle for Cloud Health Office. " +
                     "Handles encounter submission, batch dispatch, 837 generation/download, " +
                     "payer acknowledgment tracking, and correction/resubmission workflows."
    });
    c.UseInlineDefinitionsForEnums();
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "encounters:read";
    auth.DefaultWritePermission = "encounters:write";
});

// Database — MongoDB is the only encounter store (ADR 006); the client is
// registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Encounter Service requires a database. Configure MongoDb:ConnectionString.");
}

if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Encounter Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<IEncounterRepository, EncounterRepositoryMongo>();

// FHIR R4 Encounter projection and its own search query (ADR 024). The FHIR
// read and search are PHI reads, so they are recorded in the record access log
// like member-service's Patient projection.
builder.Services.AddScoped<IEncounterFhirSearchRepository, EncounterFhirSearchRepositoryMongo>();
builder.Services.AddSingleton<IEncounterFhirProjector, EncounterFhirProjector>();
builder.Services.AddRecordAccessAudit();

// 837 EDI generator
builder.Services.AddScoped<IEncounter837Service, Encounter837Service>();

// HTTP context accessor (repositories read the token tenant from HttpContext.Items)
builder.Services.AddHttpContextAccessor();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Encounter Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();


// Authentication, then tenant from the token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
