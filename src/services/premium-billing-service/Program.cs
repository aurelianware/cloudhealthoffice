using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using PremiumBillingService.Middleware;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

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
        Title = "Premium Billing Service API",
        Version = "v1",
        Description = "Premium billing for Cloud Health Office. " +
                     "Generates monthly premium invoices to sponsor groups (employers) for insurance premiums, " +
                     "tracks payments, handles retroactive adjustments, and manages delinquency."
    });
});

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB when MongoDb:ConnectionString is present, Cosmos DB otherwise
if (!string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
        new MongoClient(sp.GetRequiredService<IConfiguration>()["MongoDb:ConnectionString"]));

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var dbName = sp.GetRequiredService<IConfiguration>()["MongoDb:DatabaseName"] ?? "PremiumBillingDB";
        return client.GetDatabase(dbName);
    });

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

// Add HttpClients for service-to-service communication
builder.Services.AddHttpClient("CoverageService", client =>
{
    var coverageServiceUrl = builder.Configuration["CoverageService:BaseUrl"] ?? "http://coverage-service:8080";
    client.BaseAddress = new Uri(coverageServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("SponsorService", client =>
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

// Multi-tenant middleware (extract TenantId from JWT or headers)
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
