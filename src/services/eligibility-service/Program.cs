using CloudHealthOffice.Infrastructure.Edi.Core;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Extensions;
using EligibilityService;
using EligibilityService.Adapters;
using EligibilityService.Repositories;
using EligibilityService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Add services
builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - Eligibility Service API",
        Version = "v1",
        Description = "Real-time eligibility verification (270/271 EDI transactions)"
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
// 270-style inquiries sent as POST only read, so they carry [RequirePermission("eligibility:check")].
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "eligibility:check";
    auth.DefaultWritePermission = "settings:manage";
});

// Database Configuration — MongoDB is the only eligibility store (ADR 006);
// the database client is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Eligibility Service requires a database. Configure MongoDb:ConnectionString.");
}
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Eligibility Service supports only MongoDB. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<IEligibilityRepository, EligibilityRepositoryMongo>();

// HTTP Client for service calls (shared by adapters, factory, and eligibility service)
builder.Services.AddHttpClient("EligibilityDefault")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

// Eligibility adapters — each tenant can be configured to use a different platform.
// Registered as singletons with IHttpClientFactory injected (not HttpClient) so that
// handler rotation and DNS refresh work correctly over the application lifetime.
builder.Services.AddSingleton<IEligibilityAdapter, ChoEligibilityAdapter>();
builder.Services.AddSingleton<IEligibilityAdapter, AvailityEligibilityAdapter>();
builder.Services.AddSingleton<IEligibilityAdapter, ChangeHealthcareEligibilityAdapter>();
builder.Services.AddSingleton<EligibilityAdapterFactory>();

// Vendor-neutral healthcare transaction gateway + canonical payer reference.
// Registers Mock, Stedi (when configured), and IPayerReferenceService.
// The existing EligibilityAdapterFactory flow above is unchanged.
builder.Services.AddChoHealthcareGateways(builder.Configuration);
// The Mock gateway (the default) answers 270s with made-up eligibility.
if (string.Equals(builder.Configuration["HealthcareTransactions:DefaultGateway"] ?? "Mock", "Mock", StringComparison.OrdinalIgnoreCase))
{
    CloudHealthOffice.Infrastructure.Security.ChoSyntheticData.EnsureAllowed(
        builder.Environment, builder.Configuration, "HealthcareTransactions:DefaultGateway=Mock");
}

// Payer-side eligibility responder (inbound 270-equivalent). Distinct from
// IEligibilityGateway (CHO → external payer). Stedi inbound routing is
// adapter-ready only; the canonical / X12 development routes exercise the
// responder today.
builder.Services.AddChoPayerEligibilityResponder(builder.Configuration);
builder.Services.AddChoPayerClaimAttachmentReceiver(builder.Configuration);

builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddHttpClient<IEligibilityService, EligibilityServiceImpl>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });
// (A second AddScoped<IEligibilityService, EligibilityServiceImpl> used to
// follow and replace the typed client: the service then got a plain client
// without this 5 s timeout.)

// 270/271 EDI services
builder.Services.AddScoped<IEdi270Parser, Edi270Parser>();
builder.Services.AddScoped<IEdi271Generator, Edi271Generator>();

// X12 interchange control: ISA/IEA validation, TA1, duplicate ISA13, outbound 271 TA1 tracking.
builder.Services.AddChoX12Interchange(builder.Configuration);

// Real-time 270 → 271, shared by POST api/eligibility/270 and the CAQH CORE
// endpoint (/core/eligibility, mapped only when CoreConnectivity:Enabled).
builder.Services.AddScoped<IRealTime270Service, RealTime270Service>();
builder.Services.AddChoCoreConnectivity(builder.Configuration);
builder.Services.AddScoped<ICoreRealTimeHandler, Core270Handler>();

// Temporal coverage can be real even when historical accumulator data is not.
// Production defaults to explicit unavailable values, never synthetic zeroes.
builder.Services.AddTemporalAccumulatorSource(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ITemporalEligibilityService, TemporalEligibilityService>();

// Shared messaging bus. Backend (ServiceBus / InMemory / Null) is resolved
// from Messaging:* config + environment by AddChoMessaging.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// Batch eligibility storage (in-memory for dev, Mongo+Blob+IMessageBus for
// production). Resolution logic lives in
// BatchEligibilityServiceCollectionExtensions.
builder.Services.AddBatchEligibilityStorage(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IBatchEligibilityService, BatchEligibilityService>();
builder.Services.AddHostedService<BatchEligibilityQueueWorker>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

// Health checks (MongoDB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Eligibility Service API v1");
    });
}

app.UseChoAuthentication();
app.MapControllers();
app.MapChoX12InterchangeEndpoints("api/eligibility/interchange");
if (app.Configuration.GetValue<bool>("CoreConnectivity:Enabled"))
    app.MapChoCoreConnectivity("core/eligibility", "eligibility:check");
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
