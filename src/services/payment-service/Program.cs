using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using PaymentService.Middleware;
using PaymentService.Repositories;
using PaymentService.Services;
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

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it. Reads need payments:read; preparing runs (create, cancel) needs
// payments:run. Releasing money (executing a payment or reversal run, which
// creates the payments, writes the 835s and finalizes or voids the claims)
// needs payments:approve from a user who did not create the run (maker-checker,
// RunSeparationOfDuties); a service token cannot release money. Ledger changes
// to a payment record (record, post, reconcile) need finance:write.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "payments:read";
    auth.DefaultWritePermission = "payments:run";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Payment Service API",
        Version = "v1",
        Description = "835 ERA (Electronic Remittance Advice) payment processing for Cloud Health Office. " +
                     "Handles payment posting, reconciliation, and claim remittance tracking."
    });
});

// HTTP context accessor (repositories read the token tenant from HttpContext.Items)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB when MongoDb:ConnectionString is present, Cosmos DB otherwise
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IPaymentRepository, PaymentRepositoryMongo>();
    builder.Services.AddScoped<IPaymentRunRepository, PaymentRunRepositoryMongo>();
    builder.Services.AddScoped<IReversalRunRepository, ReversalRunRepositoryMongo>();
    builder.Services.AddScoped<IEraEnvelopeRepository, EraEnvelopeRepositoryMongo>();
    builder.Services.AddScoped<IClaimReservationRepository, ClaimReservationRepositoryMongo>();
    builder.Services.AddScoped<IReservationAuditLog, ReservationAuditLogMongo>();
    builder.Services.AddScoped<IProviderReceivableRepository, ProviderReceivableRepositoryMongo>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"];
        var key = config["CosmosDb:Key"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");

        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        });
    });

    builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
    builder.Services.AddScoped<IPaymentRunRepository, PaymentRunRepository>();
    builder.Services.AddScoped<IReversalRunRepository, ReversalRunRepository>();
    builder.Services.AddSingleton<IClaimReservationRepository, ClaimReservationRepositoryCosmos>();
    builder.Services.AddSingleton<IReservationAuditLog, ReservationAuditLogCosmos>();
    // EraEnvelope persistence on Cosmos-only deployments uses the
    // in-memory fallback. payment-service's canonical store is Mongo;
    // Cosmos paths are dev-only and don't need durable EraEnvelope storage.
    builder.Services.AddSingleton<IEraEnvelopeRepository, InMemoryEraEnvelopeRepository>();
    // Same for the provider receivable ledger: durable on Mongo only.
    builder.Services.AddSingleton<IProviderReceivableRepository, InMemoryProviderReceivableRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Provider receivables: opened by reversal runs whose 835 nets below zero
// (PLB FB), recovered by later payment runs as a positive PLB (FB, or WO with
// Receivables:RecoveryAdjustmentCode) that lowers BPR02 and the EFT credit.
builder.Services.AddScoped<IProviderReceivableLedger, ProviderReceivableLedger>();

// FFS EFT: the payee's approved bank account (dual control) is read from
// provider-service's service-only payee read, with payment-service's own token
// inside an approved run execution or EFT-file generation (RunExecutionGrant).
// Without ProviderService:BaseUrl no account is read: execution keeps the run's
// payment method and EFT-file generation refuses.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ProviderService:BaseUrl"]))
{
    builder.Services.AddHttpClient(HttpProviderPayeeAccountSource.HttpClientName, client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["ProviderService:BaseUrl"]!);
        client.Timeout = TimeSpan.FromSeconds(10);
    }).AddRunExecutionServiceToken();
    builder.Services.AddScoped<IProviderPayeeAccountSource, HttpProviderPayeeAccountSource>();
}
else
{
    builder.Services.AddSingleton<IProviderPayeeAccountSource, UnconfiguredProviderPayeeAccountSource>();
}
builder.Services.AddScoped<IFfsEftFileService, FfsEftFileService>();

// Bank transmission of a completed ACH run's NACHA file. Off unless
// BankTransmission:Enabled is true (and the tenant's own
// paymentControls.nachaTransmission.enabled in tenant-service). While off, no
// SFTP, Key Vault or tenant-service client is even registered: the service
// refuses before doing anything. See docs/operations/NACHA-BANK-TRANSMISSION-RUNBOOK.md.
builder.Services.Configure<BankTransmissionOptions>(builder.Configuration.GetSection(BankTransmissionOptions.SectionName));
// Effective entry dates (bank time zone, Federal Reserve calendar): chosen when a
// run is created and when its NACHA file is pinned, checked when it is sent.
builder.Services.AddSingleton<AchEffectiveDatePolicy>();
if (builder.Configuration.GetValue<bool>($"{BankTransmissionOptions.SectionName}:Enabled"))
{
    CloudHealthOffice.NachaTransmission.NachaTransmissionServiceCollectionExtensions.AddChoNachaTransmission(
        builder.Services, builder.Configuration, builder.Environment, "payment-service", databaseProvider);
}
else
{
    builder.Services.AddSingleton<CloudHealthOffice.NachaTransmission.INachaTransmitter, DisabledNachaTransmitter>();
    builder.Services.AddSingleton<CloudHealthOffice.NachaTransmission.INachaRemoteFileProbe, DisabledNachaTransmitter>();
}
if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IPaymentFileTransmissionRepository, PaymentFileTransmissionRepositoryMongo>();
    builder.Services.AddScoped<INachaFileIdModifierAllocator, NachaFileIdModifierAllocatorMongo>();
}
else
{
    builder.Services.AddSingleton<IPaymentFileTransmissionRepository, PaymentFileTransmissionRepositoryCosmos>();
    builder.Services.AddSingleton<INachaFileIdModifierAllocator, NachaFileIdModifierAllocatorCosmos>();
}
builder.Services.AddScoped<IPaymentFileTransmissionService, PaymentFileTransmissionService>();

// Services
builder.Services.AddScoped<IPaymentRunService, PaymentRunService>();
builder.Services.AddScoped<IReversalRunService, ReversalRunService>();
builder.Services.AddScoped<IEraGeneratorService, EraGeneratorService>();

// 5.10 — batched 835 generation. Stateless services, Singleton DI.
builder.Services.AddSingleton<IBatchEraGeneratorService, BatchEraGeneratorService>();
builder.Services.AddSingleton<ICarcRarcMappingService, CarcRarcMappingService>();
builder.Services.AddScoped<ITradingPartnersClient, TradingPartnersClient>();
builder.Services.AddScoped<IRunSeparationOfDuties, RunSeparationOfDuties>();

// Stranded claim reservations (a run reserved a claim, then failed or was
// cancelled without paying it). The hosted job releases only the safe case
// (run Failed/Cancelled, past PaymentRuns:ReservationGracePeriod, no payment
// and no 835 in payment-service) as payment-service itself, per tenant, with
// no outbound calls; everything else is flagged NeedsAttention for a second
// approver (POST /api/{paymentruns|reversalruns}/{id}/reservations/{claimId}/release).
builder.Services.Configure<PaymentService.Models.ReservationReconciliationOptions>(
    builder.Configuration.GetSection(PaymentService.Models.ReservationReconciliationOptions.SectionName));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<IReservationReconciliationService, ReservationReconciliationService>();
builder.Services.AddSingleton<ReservationReconciliationJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReservationReconciliationJob>());

// Run-execution clients. claims-service (search, read, remittance, void,
// adjustments) and trading-partner-service are called only while a payment or
// reversal run is executed (or its finalizes retried), after the caller passed
// payments:approve and separation of duties. Those calls carry payment-service's
// own service token for the run's tenant, never the approver's token
// (FinanceApprover holds no claims permissions): AddRunExecutionServiceToken
// removes the shared forwarding handler from these clients and mints the token
// only inside an open RunExecutionGrant. The approver is recorded on the run,
// payments and 835s. Every other factory client keeps ChoOutboundTokenHandler.
builder.Services.AddHttpClient(ClaimsServiceClient.HttpClientName, client =>
{
    var claimsServiceUrl = builder.Configuration["ClaimsService:BaseUrl"] ?? "http://claims-service:8080";
    client.BaseAddress = new Uri(claimsServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
}).AddRunExecutionServiceToken();

// 5.10 — typed HttpClient for trading-partner-service NPI lookups
// during PaymentRun execution. 10s timeout matches credentialing/
// resolution-client conventions; trading-partner-service is a
// low-frequency dependency so a slow lookup shouldn't fail an entire
// PaymentRun outright.
builder.Services.AddHttpClient(TradingPartnersClient.HttpClientName, client =>
{
    // The cluster Service listens on port 80 (targetPort 8080); the former
    // default http://trading-partner-service:8080 addressed the container port
    // through the Service and was refused in the cluster. docker-compose sets
    // TradingPartnerService__BaseUrl to the container port explicitly.
    var tradingPartnerUrl = builder.Configuration["TradingPartnerService:BaseUrl"]
        ?? TradingPartnersClient.DefaultBaseUrl;
    client.BaseAddress = new Uri(tradingPartnerUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddRunExecutionServiceToken();

// Health checks (MongoDB or Cosmos DB, claims-service HTTP)
var claimsServiceHealthUrl = builder.Configuration["ClaimsService:BaseUrl"] ?? "http://claims-service:8080";
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
    options.HttpDependencies["claims-service"] = $"{claimsServiceHealthUrl.TrimEnd('/')}/health/live";
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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();


// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Enable WebApplicationFactory<Program> access from test projects
public partial class Program { }
