using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Middleware;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
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
// it. Reads need billing:read, writes billing:run. Actions that move money or
// change a sponsor's status are annotated with stricter permissions in the
// controllers (finance:write for ledger changes and delinquency suspension,
// payments:approve for releasing debits to the bank). The Stripe webhook is
// anonymous and authenticated by its Stripe signature.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "billing:read";
    auth.DefaultWritePermission = "billing:run";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Premium Billing Service API",
        Version = "v1",
        Description = "Premium billing for Cloud Health Office. " +
                     "Generates monthly premium invoices to sponsor groups (employers) for insurance premiums, " +
                     "tracks payments, handles retroactive adjustments, and manages delinquency."
    });
});

// HTTP context accessor (repositories read the token tenant from HttpContext.Items)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB when MongoDb:ConnectionString is present, Cosmos DB otherwise
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IPremiumInvoiceRepository, PremiumInvoiceRepositoryMongo>();
    builder.Services.AddScoped<IBillingRunRepository, BillingRunRepositoryMongo>();
    builder.Services.AddScoped<IEftDraftRepository, EftDraftRepositoryMongo>();
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

    builder.Services.AddScoped<IPremiumInvoiceRepository, PremiumInvoiceRepository>();
    builder.Services.AddScoped<IBillingRunRepository, BillingRunRepository>();
    builder.Services.AddScoped<IEftDraftRepository, EftDraftRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Services
builder.Services.AddScoped<IPremiumBillingService, PremiumBillingService.Services.PremiumBillingService>();
builder.Services.AddSingleton<INachaFileService, NachaFileService>();
builder.Services.AddScoped<IStripeAchService, StripeAchService>();
builder.Services.AddScoped<IEftDraftService, EftDraftService>();
builder.Services.AddScoped<ISponsorServiceClient, SponsorServiceClient>();
builder.Services.AddScoped<ICoverageServiceClient, CoverageServiceClient>();
// Sponsor bank details have no system of record (sponsor-service stores none),
// so every auto-debit attempt is refused as needing attention rather than
// silently treated as "not enrolled". See UnavailableSponsorBankAccountSource.
builder.Services.AddSingleton<ISponsorBankAccountSource, UnavailableSponsorBankAccountSource>();

// HttpClients for service-to-service communication. AddChoAuthentication puts
// ChoOutboundTokenHandler on every factory client: a caller's token is
// forwarded, and with no caller a service token is minted for the tenant the
// request names in X-Tenant-ID (the clients always set it from the record).
builder.Services.AddHttpClient(CoverageServiceClient.HttpClientName, client =>
{
    var coverageServiceUrl = builder.Configuration["CoverageService:BaseUrl"] ?? "http://coverage-service:8080";
    client.BaseAddress = new Uri(coverageServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient(SponsorServiceClient.HttpClientName, client =>
{
    var sponsorServiceUrl = builder.Configuration["SponsorService:BaseUrl"] ?? "http://sponsor-service:8080";
    client.BaseAddress = new Uri(sponsorServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Premium Billing Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
