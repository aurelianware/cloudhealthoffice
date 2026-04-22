using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using ConsentService.HostedServices;
using ConsentService.Middleware;
using ConsentService.Repositories;
using ConsentService.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — same bootstrap shape as all
// 35 services in the platform.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Consent Service API",
        Version = "v1",
        Description = "Records, queries, and revokes HIPAA §164.508 authorization records with field-level encryption and an append-only audit trail."
    });
});

// ── Database Configuration ───────────────────────────────────────────
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(_ =>
        new MongoDB.Driver.MongoClient(mongoConnectionString));

    builder.Services.AddSingleton<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var databaseName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(databaseName);
    });

    builder.Services.AddSingleton<IConsentEventRepository, ConsentEventRepositoryMongo>();
    builder.Services.AddSingleton<IConsentEventSink>(sp => (IConsentEventSink)sp.GetRequiredService<IConsentEventRepository>());
    builder.Services.AddSingleton<IConsentRepository, ConsentRepositoryMongo>();

    builder.Services.AddHostedService<ConsentIndexInitializer>();

    Console.WriteLine("[consent-service] Using MongoDB database provider");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"]
            ?? throw new InvalidOperationException("CosmosDb:Endpoint configuration missing");
        var key = configuration["CosmosDb:Key"]
            ?? throw new InvalidOperationException("CosmosDb:Key configuration missing");

        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        });
    });

    builder.Services.AddScoped<IConsentEventRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new ConsentEventRepository(cosmosClient, databaseName);
    });
    builder.Services.AddScoped<IConsentEventSink>(sp => (IConsentEventSink)sp.GetRequiredService<IConsentEventRepository>());
    builder.Services.AddScoped<IConsentRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var sink = sp.GetRequiredService<IConsentEventSink>();
        return new ConsentRepository(cosmosClient, databaseName, sink);
    });

    Console.WriteLine("[consent-service] Using Cosmos DB database provider");
}

// ── Consent body encryption ─────────────────────────────────────────
// Non-dev startup guard: no ConsentEncryption section = startup error.
// Only IsDevelopment() falls back to the no-op encryptor.
var consentEncryptionSection = builder.Configuration.GetSection(ConsentEncryptionOptions.SectionName);
if (consentEncryptionSection.Exists())
{
    var options = consentEncryptionSection.Get<ConsentEncryptionOptions>() ?? new ConsentEncryptionOptions();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IConsentFieldEncryptor>(sp =>
        new ConsentFieldEncryptor(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ILogger<ConsentFieldEncryptor>>(),
            options));

    Console.WriteLine(
        $"[consent-service] IConsentFieldEncryptor = KeyVault (rotating) — current: {options.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", options.AcceptedKeyVersions)}]");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "ConsentEncryption must be configured in non-development environments. " +
            "Publish the consent-body-encryption-key-v1 secret and set ConsentEncryption:KeySecretPrefix / CurrentKeyVersion.");
    }
    // Dev-only passthrough. Still register a default options so the health
    // check and other key-aware services have a well-formed config.
    builder.Services.AddSingleton(new ConsentEncryptionOptions());
    builder.Services.AddSingleton<IConsentFieldEncryptor, NoOpConsentFieldEncryptor>();
    Console.WriteLine("[dev] IConsentFieldEncryptor = NoOp (consent body fields stored plaintext). Configure ConsentEncryption to enable.");
}

// ── Kafka producer (consent status events) ──────────────────────────
// Always registered; degraded-mode-silent if Kafka:BootstrapServers is unset.
builder.Services.AddSingleton<ConsentEventPublisher>();
builder.Services.AddSingleton<IConsentEventPublisher>(sp => sp.GetRequiredService<ConsentEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsentEventPublisher>());

// IMessageBus — registered for future consumers; no-op cost today. (Not a
// Kafka facade; kept separate by design.)
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Health checks: Mongo / Cosmos via the shared bootstrap, plus the
// local consent-encryption-key readiness check. Local to consent-service
// until a second service needs it.
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
})
.AddCheck<ConsentEncryptionKeyHealthCheck>(
    "consent-encryption-key",
    failureStatus: HealthStatus.Unhealthy,
    tags: new[] { "ready" });

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseTenantContext();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
