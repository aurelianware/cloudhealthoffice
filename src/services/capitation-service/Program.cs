using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using CapitationService.Middleware;
using CapitationService.Repositories;
using CapitationService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.NachaTransmission;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// One-off migration (operator CLI): dotnet run -- --migrate-split
if (args.Contains("--migrate-split"))
{
    Environment.ExitCode = await CapitationService.Migrations.SplitCapitationContracts.RunAsync(
        builder.Configuration["MongoDb:ConnectionString"] ?? string.Empty,
        builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice",
        builder.Configuration["ProviderContractsService:BaseUrl"] ?? "http://provider-contracts-service:8080",
        builder.Configuration,
        builder.Environment.EnvironmentName);
    return;
}

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Capitation Service API",
        Version = "v1",
        Description = "Per-member-per-month (PMPM) capitation payments from health plans to providers. " +
                     "Manages capitation contracts, generates monthly payment statements, " +
                     "and disburses payments via NACHA ACH credits or Stripe Connect transfers."
    });
});

// HTTP context accessor (repositories read the token tenant from HttpContext.Items)
builder.Services.AddHttpContextAccessor();

// CHO token authentication: tenant from the token, actor from the token, default deny.
// Approving, voiding and releasing payments additionally need payments:approve.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "payments:read";
    auth.DefaultWritePermission = "payments:run";
});

// Database Configuration — MongoDB when MongoDb:ConnectionString is present, Cosmos DB otherwise
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<ICapitationContractRepository, CapitationContractRepositoryMongo>();
    builder.Services.AddScoped<ICapitationRunRepository, CapitationRunRepositoryMongo>();
    builder.Services.AddScoped<ICapitationStatementRepository, CapitationStatementRepositoryMongo>();
    builder.Services.AddScoped<ICapitationDisbursementRepository, CapitationDisbursementRepositoryMongo>();
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

    builder.Services.AddScoped<ICapitationContractRepository, CapitationContractRepository>();
    builder.Services.AddScoped<ICapitationRunRepository, CapitationRunRepository>();
    builder.Services.AddScoped<ICapitationStatementRepository, CapitationStatementRepository>();
    builder.Services.AddScoped<ICapitationDisbursementRepository, CapitationDisbursementRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Services
// Maker-checker on approving and releasing payments. On for every tenant unless the
// tenant's configuration.paymentControls.enforceSeparationOfDuties is false in tenant-service.
builder.Services.AddSingleton<ITenantPaymentControls, TenantPaymentControls>();
builder.Services.AddScoped<IPaymentSeparationOfDuties, PaymentSeparationOfDuties>();
builder.Services.AddScoped<ICapitationRunService, CapitationRunService>();
builder.Services.AddSingleton<INachaCreditFileService, NachaCreditFileService>();
builder.Services.AddSingleton<IStripeTransferClient, StripeTransferClient>();
builder.Services.AddScoped<IStripeConnectService, StripeConnectService>();
// Full provider routing/account numbers for NACHA credits come from
// provider-service's service-only read of the active approved account (dual
// control there), fetched with capitation-service's own token after the
// releasing user passed payments:approve and separation of duties. No approved
// account or a refusal leaves the disbursement needing attention.
builder.Services.AddScoped<IProviderBankAccountSource, HttpProviderBankAccountSource>();
// NACHA credit files go from this service straight to the tenant's bank (SFTP
// settings from tenant-service paymentControls.nachaTransmission, credentials
// from Key Vault, pinned host key). No person receives the file. One that
// cannot be sent is held encrypted (FieldProtection key ring) for 7 days for a
// platform admin's retrieval or another approver's retry.
builder.Services.AddChoFieldProtection(builder.Configuration, builder.Environment, "capitation-service");
builder.Services.AddChoNachaTransmission(builder.Configuration, builder.Environment, "capitation-service", databaseProvider);
builder.Services.AddScoped<ICapitationDisbursementService, CapitationDisbursementService>();
builder.Services.AddSingleton<ICapitationEraService, CapitationEraService>();

// Add HttpClients for service-to-service communication
builder.Services.AddHttpClient("CoverageService", client =>
{
    var coverageServiceUrl = builder.Configuration["CoverageService:BaseUrl"] ?? "http://coverage-service:8080";
    client.BaseAddress = new Uri(coverageServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("ProviderService", client =>
{
    var providerServiceUrl = builder.Configuration["ProviderService:BaseUrl"] ?? "http://provider-service:8080";
    client.BaseAddress = new Uri(providerServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// provider-service's full bank-account read: HttpProviderBankAccountSource sets
// capitation-service's own service token on every request (never the user's).
builder.Services.AddHttpClient(HttpProviderBankAccountSource.HttpClientName, client =>
{
    var providerServiceUrl = builder.Configuration["ProviderService:BaseUrl"] ?? "http://provider-service:8080";
    client.BaseAddress = new Uri(providerServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("RiskAdjustmentService", client =>
{
    var riskServiceUrl = builder.Configuration["RiskAdjustmentService:BaseUrl"] ?? "http://risk-adjustment-service:8080";
    client.BaseAddress = new Uri(riskServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient(TenantPaymentControls.HttpClientName, client =>
{
    var tenantServiceUrl = builder.Configuration["TenantService:BaseUrl"] ?? "http://tenant-service";
    client.BaseAddress = new Uri(tenantServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddChoServiceAuthentication();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Capitation Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication, tenant from the token only, then permission policies.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
