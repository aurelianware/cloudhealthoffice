using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using EnrollmentImportService;
using CloudHealthOffice.Infrastructure.Extensions;
using EnrollmentImportService.Clients;
using EnrollmentImportService.HostedServices;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using EnrollmentImportService.Services.Runs;
using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Add services
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// that token, never from X-Tenant-ID or the request body.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "enrollment:read";
    auth.DefaultWritePermission = "enrollment:process";
});

// MongoDB is the only enrollment store (ADR 006); the database client is
// registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Enrollment Import Service requires a database. Configure MongoDb:ConnectionString.");
}
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Enrollment Import Service supports only MongoDB. Remove Database:Provider or set it to MongoDb.");
}
// Repositories and services. Constructed without I/O side effects (index
// creation happens in EnrollmentIndexInitializer below), so these can be
// singletons rather than scoped — same pattern as member-service.
builder.Services.AddSingleton<IEnrollmentTransactionRepository, EnrollmentTransactionRepository>();
builder.Services.AddSingleton<IEnrollmentImportRunRepository, EnrollmentImportRunRepository>();
builder.Services.AddSingleton<IEnrollmentEventRepository, EnrollmentEventRepository>();
builder.Services.AddScoped<IEnrollmentEventPublisher, EnrollmentEventPublisher>();
builder.Services.AddSingleton<IEnrollmentValidator, EnrollmentValidator>();
builder.Services.AddSingleton<IEnrollmentExceptionRepository, EnrollmentExceptionRepository>();
// The orphaned-dependent grace period and retry (enrollment-operations.md §10,
// decision 5). The grace period is a plan setting read from tenant-service
// (docs/architecture/plan-configuration.md): line of business → sponsor.
builder.Services.AddPlanSettingsClient(builder.Configuration);
builder.Services.AddSingleton<EnrollmentImportService.Services.Orphans.IOrphanGracePolicy, EnrollmentImportService.Services.Orphans.OrphanGracePolicy>();
builder.Services.AddScoped<EnrollmentImportService.Services.Orphans.OrphanDependentRetrier>();
// Every import path goes through the orphan retry: the import itself, then a
// retry of dependents waiting on the subscribers it put on file.
builder.Services.AddScoped<EnrollmentImportService.Services.EnrollmentImportService>();
builder.Services.AddScoped<IEnrollmentImportService, EnrollmentImportService.Services.Orphans.OrphanRetryingImportService>();
builder.Services.AddScoped<EnrollmentExceptionService>();
builder.Services.Configure<EnrollmentImportService.Services.Orphans.OrphanSweepOptions>(
    builder.Configuration.GetSection(EnrollmentImportService.Services.Orphans.OrphanSweepOptions.SectionName));
builder.Services.AddHostedService<EnrollmentImportService.Services.Orphans.OrphanSweepService>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEnrollment834EdiParser, Enrollment834EdiParser>();

// X12 interchange control: ISA/IEA validation, TA1, duplicate ISA13 (shared library).
builder.Services.AddChoX12Interchange(builder.Configuration);
builder.Services.AddSingleton<IPlanCodeGapReportService, PlanCodeGapReportService>();

// Raw 834s of every size: staged in the raw-file store, imported as a run of
// units by a background worker (docs/architecture/edi-operations.md §11).
builder.Services.Configure<EnrollmentImportRunOptions>(builder.Configuration.GetSection(EnrollmentImportRunOptions.SectionName));
var documentStoreConfigured =
    !string.IsNullOrWhiteSpace(builder.Configuration[$"{DocumentStoreServiceCollectionExtensions.SectionName}:Provider"])
    || !string.IsNullOrWhiteSpace(builder.Configuration[DocumentStoreServiceCollectionExtensions.LegacyAzureConnectionStringKey]);
if (documentStoreConfigured)
{
    // The configured object store (Azure Blob, Google Cloud Storage or S3) is the raw-file archive.
    builder.Services.AddChoDocumentStore(builder.Environment.EnvironmentName);
    builder.Services.AddSingleton<IRawEdiFileStore, DocumentStoreRawEdiFileStore>();
}
else
{
    // No object store configured: GridFS in the service's own MongoDB, shared by every replica.
    builder.Services.AddSingleton<IRawEdiFileStore, GridFsRawEdiFileStore>();
}
builder.Services.AddSingleton<IStreaming834Parser, Streaming834Parser>();
builder.Services.AddSingleton<IEnrollmentImportUnitStore, EnrollmentImportUnitStore>();
builder.Services.AddScoped<EnrollmentRunFinisher>();
builder.Services.AddSingleton<IEnrollmentUnitApplier, EnrollmentUnitApplier>();
builder.Services.AddSingleton<IEnrollmentUnitDispatcher, InProcessEnrollmentUnitDispatcher>();
builder.Services.AddScoped<EnrollmentImportRunProcessor>();
builder.Services.AddSingleton<EnrollmentImportRunSignal>();
builder.Services.AddSingleton<IEnrollmentImportRunScheduler>(sp => sp.GetRequiredService<EnrollmentImportRunSignal>());
builder.Services.AddScoped<Raw834Intake>();
builder.Services.AddHostedService<EnrollmentImportRunWorker>();
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = EnrollmentImportService.Controllers.EnrollmentController.MaxRawUploadBytes);

builder.Services.AddHostedService<EnrollmentIndexInitializer>();

// Downstream CHO clients. AddChoAuthentication puts ChoOutboundTokenHandler on
// every IHttpClientFactory client: inside a request it forwards the caller's
// token; with no caller it mints a service token for the X-Tenant-ID each
// client sets on its requests. Never construct an HttpClient directly here.
//
// member-service / sponsor-service clients — enrollment-import-service used
// to write Member/Sponsor documents directly into Mongo collections that
// collide with the ones those now-split-out services actually own (see
// IMemberServiceClient's doc comment). Delegating via HTTP instead.
//
// Fallback URLs are the k8s Service's actual port (80, not the container's
// 8080) — confirmed live: claims-service's own HttpMemberResolver has this
// exact ":8080" mistake in its code fallback too, silently masked in every
// environment by an explicit Services__MemberService=http://member-service
// env var override. Not repeating that here; see the deployment yaml for
// the belt-and-suspenders explicit env vars.
builder.Services.AddHttpClient(HttpMemberServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:MemberService"]
        ?? "http://member-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IMemberServiceClient, HttpMemberServiceClient>();

builder.Services.AddHttpClient(HttpSponsorServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:SponsorService"]
        ?? "http://sponsor-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ISponsorServiceClient, HttpSponsorServiceClient>();

// benefit-plan-service client — resolves a trading partner's 834 plan code
// (HD04) to this platform's canonical PlanId before Coverage is written.
builder.Services.AddHttpClient(HttpBenefitPlanServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanService"]
        ?? "http://benefit-plan-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IBenefitPlanServiceClient, HttpBenefitPlanServiceClient>();

// coverage-service client — Coverage used to be the one entity this service
// still wrote directly into a Mongo collection shared with coverage-service's
// own repository. Delegating via HTTP now that PlanId is actually resolved.
builder.Services.AddHttpClient(HttpCoverageServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:CoverageService"]
        ?? "http://coverage-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ICoverageServiceClient, HttpCoverageServiceClient>();

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

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();
app.MapChoX12InterchangeEndpoints("api/v1/enrollment/interchange");
app.MapChoEdiFileEndpoints("api/v1/enrollment/edi");

app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
