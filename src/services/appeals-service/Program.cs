using AppealsService.HostedServices;
using AppealsService.Middleware;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;

// ── BSON convention: enums stored as strings ─────────────────────────
// Register BEFORE any IMongoClient is constructed. Ensures enum values on
// Appeal (Status, ClosureReasonCode, AppealType, etc.) are persisted as
// human-readable strings rather than integers. Matches the Cosmos
// serializer's enum representation (JsonStringEnumConverter), so the two
// storage backends produce comparable shapes. Also future-proofs against
// enum-value reordering: adding a new status value doesn't silently
// re-map existing data.
MongoDB.Bson.Serialization.Conventions.ConventionRegistry.Register(
    "appeals-enums-as-strings",
    new ConventionPack { new EnumRepresentationConvention(BsonType.String) },
    _ => true);

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — same bootstrap shape as
// consent-service / personal-rep-service.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cloud Health Office - Appeals Service API",
        Version = "v1",
        Description = "Claim appeals processing with 275 attachment support. "
                    + "State-machine lifecycle, append-only audit trail, field-level encryption."
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

    builder.Services.AddSingleton<IAppealEventRepository, AppealEventRepositoryMongo>();
    builder.Services.AddSingleton<IAppealEventSink>(sp => (IAppealEventSink)sp.GetRequiredService<IAppealEventRepository>());
    builder.Services.AddSingleton<IAppealRepository, AppealRepositoryMongo>();

    // Registration order matters for IHostedService.StartAsync sequencing:
    // migration runs first so the index initializer sees a consistent
    // schema and duplicate-AppealNumber warnings fire before the unique
    // index build.
    builder.Services.AddHostedService<AppealStatusMigrationHostedService>();
    builder.Services.AddHostedService<AppealIndexInitializer>();

    Console.WriteLine("[appeals-service] Using MongoDB database provider");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"]
            ?? configuration["CosmosDb:AccountEndpoint"]
            ?? throw new InvalidOperationException("CosmosDb:Endpoint configuration missing");
        var key = configuration["CosmosDb:Key"]
            ?? configuration["CosmosDb:AccountKey"]
            ?? throw new InvalidOperationException("CosmosDb:Key configuration missing");

        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        });
    });

    builder.Services.AddScoped<IAppealEventRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new AppealEventRepository(cosmosClient, databaseName);
    });
    builder.Services.AddScoped<IAppealEventSink>(sp => (IAppealEventSink)sp.GetRequiredService<IAppealEventRepository>());
    builder.Services.AddScoped<IAppealRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var sink = sp.GetRequiredService<IAppealEventSink>();
        return new AppealRepository(cosmosClient, databaseName, sink);
    });

    Console.WriteLine("[appeals-service] Using Cosmos DB database provider");
}

// ── Appeal body encryption ───────────────────────────────────────────
// Non-dev startup guard: no AppealEncryption section = startup error.
// Only IsDevelopment() falls back to the no-op encryptor.
var appealEncryptionSection = builder.Configuration.GetSection(AppealEncryptionOptions.SectionName);
if (appealEncryptionSection.Exists())
{
    var options = appealEncryptionSection.Get<AppealEncryptionOptions>() ?? new AppealEncryptionOptions();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IAppealFieldEncryptor>(sp =>
        new AppealFieldEncryptor(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ILogger<AppealFieldEncryptor>>(),
            options));

    Console.WriteLine(
        $"[appeals-service] IAppealFieldEncryptor = KeyVault (rotating) — current: {options.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", options.AcceptedKeyVersions)}]");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "AppealEncryption must be configured in non-development environments. " +
            "Publish the appeal-body-encryption-key-v1 secret and set AppealEncryption:KeySecretPrefix / CurrentKeyVersion.");
    }
    builder.Services.AddSingleton(new AppealEncryptionOptions());
    builder.Services.AddSingleton<IAppealFieldEncryptor, NoOpAppealFieldEncryptor>();
    Console.WriteLine("[dev] IAppealFieldEncryptor = NoOp (appeal body fields stored plaintext). Configure AppealEncryption to enable.");
}

// ── Kafka producer (appeal lifecycle events) ─────────────────────────
// Always registered; degraded-mode-silent if Kafka:BootstrapServers is unset.
builder.Services.AddSingleton<AppealEventPublisher>();
builder.Services.AddSingleton<IAppealEventPublisher>(sp => sp.GetRequiredService<AppealEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppealEventPublisher>());

// ── Kafka consumer (X12 275 attachment ingress) ──────────────────────
// Subscribes to the attachments-in topic, routes appeal-context 275s to
// the existing AppendAttachmentAsync path. Degraded-mode-silent if
// Kafka:BootstrapServers is unset. Registered AFTER AppealEventPublisher
// so the producer's StartAsync runs first — the consumer's per-message
// publish call expects the producer to already be available.
builder.Services.AddSingleton<Attachment275EnvelopeMapper>();
builder.Services.AddSingleton<IAttachment275DeadLetterSink, LoggingAttachment275DeadLetterSink>();
builder.Services.AddHostedService<Attachment275ConsumerHostedService>();

// IMessageBus — registered for future consumers; no-op cost today.
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
// local appeal-encryption-key readiness check.
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
})
.AddCheck<AppealEncryptionKeyHealthCheck>(
    "appeal-encryption-key",
    failureStatus: HealthStatus.Unhealthy,
    tags: new[] { "ready" });

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Appeals Service API v1");
    });
}

app.UseCors();
app.UseTenantContext();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
