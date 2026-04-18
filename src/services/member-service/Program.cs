using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using MemberService.HostedServices;
using MemberService.Middleware;
using MemberService.Repositories;
using MemberService.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Member Service API",
        Version = "v1",
        Description = "Manages health plan member data (subscribers and dependents) populated by X12 834 Enrollment transactions. Surfaces FHIR R4 Patient projection."
    });
});

// ── Database Configuration ───────────────────────────────────────────
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var eventsContainerName = builder.Configuration["CosmosDb:EventsContainerName"] ?? "member-events";
var eventsCollectionName = builder.Configuration["MongoDb:EventsCollectionName"] ?? "member-events";

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

    // Repositories are constructed without I/O side effects; indexes are provisioned
    // by the hosted services below, which lets us register these as singletons.
    builder.Services.AddSingleton<IMemberRepository, MemberRepositoryMongo>();
    builder.Services.AddSingleton<IMemberEventRepository>(sp =>
        new MemberEventRepositoryMongo(
            sp.GetRequiredService<MongoDB.Driver.IMongoDatabase>(),
            eventsCollectionName));
    builder.Services.AddSingleton<IFamilyRelationshipRepository, FamilyRelationshipRepositoryMongo>();

    builder.Services.AddHostedService<MemberIndexInitializer>();
    builder.Services.AddHostedService<FamilyRelationshipIndexInitializer>();
    builder.Services.AddSingleton<IHostedService>(sp =>
        new MemberEventIndexInitializer(
            sp.GetRequiredService<MongoDB.Driver.IMongoDatabase>(),
            eventsCollectionName,
            sp.GetRequiredService<ILogger<MemberEventIndexInitializer>>()));

    Console.WriteLine("Using MongoDB database provider");
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

    builder.Services.AddScoped<IMemberRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberRepository(cosmosClient, databaseName);
    });

    builder.Services.AddScoped<IMemberEventRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberEventRepository(
            cosmosClient,
            databaseName,
            eventsContainerName,
            sp.GetRequiredService<ILogger<MemberEventRepository>>());
    });

    builder.Services.AddScoped<IFamilyRelationshipRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new FamilyRelationshipRepository(cosmosClient, databaseName);
    });

    Console.WriteLine($"Using Cosmos DB database provider (events container: {eventsContainerName})");
}

// ── Member Service internals ─────────────────────────────────────────
builder.Services.AddScoped<IMemberEventPublisher, CosmosMemberEventPublisher>();
builder.Services.AddSingleton<IFhirPatientProjector, FhirPatientProjector>();
builder.Services.AddScoped<IFamilyRelationshipService, FamilyRelationshipService>();
builder.Services.AddScoped<IRelationshipShim, RelationshipShim>();

// Identifier encryption. Real KV-backed encryptor when a data-key secret name is
// configured; otherwise fall through to the no-op shim (dev only).
var encryptionKeySecretName = builder.Configuration["Member:IdentifierEncryption:KeySecretName"];
if (!string.IsNullOrWhiteSpace(encryptionKeySecretName))
{
    builder.Services.AddSingleton<IIdentifierEncryptor>(sp =>
        new KeyVaultIdentifierEncryptor(
            sp.GetRequiredService<ISecretProvider>(),
            sp.GetRequiredService<ILogger<KeyVaultIdentifierEncryptor>>(),
            encryptionKeySecretName));
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        // Fail loudly in non-dev rather than silently passing PII through plaintext.
        throw new InvalidOperationException(
            "Member:IdentifierEncryption:KeySecretName must be configured in non-development environments.");
    }
    builder.Services.AddSingleton<IIdentifierEncryptor, NoOpIdentifierEncryptor>();
    Console.WriteLine("[dev] IIdentifierEncryptor = NoOp (PII stored plaintext). Configure Member:IdentifierEncryption:KeySecretName to enable.");
}

// Identifier fingerprinting (HMAC-SHA256 keyed by a DISTINCT KV secret from the
// encryption key). Used to dedupe PII identifiers without comparing ciphertexts
// (which differ by AES-GCM nonce even for identical plaintext).
var fingerprintKeySecretName =
    builder.Configuration["Encryption:IdentifierFingerprintKeySecret"]
    ?? builder.Configuration["Member:IdentifierFingerprint:KeySecretName"];
if (!string.IsNullOrWhiteSpace(fingerprintKeySecretName))
{
    builder.Services.AddSingleton<IIdentifierFingerprinter>(sp =>
        new HmacSha256IdentifierFingerprinter(
            sp.GetRequiredService<ISecretProvider>(),
            sp.GetRequiredService<ILogger<HmacSha256IdentifierFingerprinter>>(),
            fingerprintKeySecretName));
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Encryption:IdentifierFingerprintKeySecret must be configured in non-development environments.");
    }
    builder.Services.AddSingleton<IIdentifierFingerprinter, NoOpIdentifierFingerprinter>();
    Console.WriteLine("[dev] IIdentifierFingerprinter = NoOp (plain SHA-256). Configure Encryption:IdentifierFingerprintKeySecret to enable.");
}

// ── Downstream typed clients ─────────────────────────────────────────
builder.Services.Configure<DownstreamOptions>(builder.Configuration.GetSection("Downstream"));

var coverageBaseUrl = builder.Configuration["Downstream:CoverageService:BaseUrl"];
var enrollmentBaseUrl = builder.Configuration["Downstream:EnrollmentImportService:BaseUrl"];
var accumulatorBaseUrl = builder.Configuration["Downstream:AccumulatorService:BaseUrl"];

RegisterDownstream<ICoverageServiceClient, HttpCoverageServiceClient, FakeCoverageServiceClient>(
    builder, coverageBaseUrl);
RegisterDownstream<IEnrollmentImportServiceClient, HttpEnrollmentImportServiceClient, FakeEnrollmentImportServiceClient>(
    builder, enrollmentBaseUrl);
// Production must have accumulator-service configured — no silent fallback to a
// fake projection. Per PR #650 review: fakes are dev-only, and an unset downstream
// in a non-development environment is a startup error, not a lazy 503 at call time.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(accumulatorBaseUrl))
{
    throw new InvalidOperationException(
        "Downstream:AccumulatorService:BaseUrl must be configured outside Development.");
}
RegisterDownstream<IAccumulatorServiceClient, HttpAccumulatorServiceClient, FakeAccumulatorServiceClient>(
    builder, accumulatorBaseUrl);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

var app = builder.Build();

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

// ── Helpers ──────────────────────────────────────────────────────────
static void RegisterDownstream<TInterface, THttpClient, TFakeClient>(
    WebApplicationBuilder builder,
    string? baseUrl)
    where TInterface : class
    where THttpClient : class, TInterface
    where TFakeClient : class, TInterface
{
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        builder.Services.AddHttpClient<TInterface, THttpClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.Timeout = TimeSpan.FromSeconds(10);
        });
    }
    else if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddSingleton<TInterface, TFakeClient>();
        Console.WriteLine($"[dev] Registered {typeof(TFakeClient).Name} for {typeof(TInterface).Name} (no downstream URL configured).");
    }
    else
    {
        // Production: register the Http client with no base URL. The client will
        // throw DownstreamUnavailableException on every call, and controllers map
        // that to a 503 ProblemDetails. Keeps the service bootable so other
        // endpoints remain available.
        builder.Services.AddHttpClient<TInterface, THttpClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });
    }
}

// Required so WebApplicationFactory<Program> works in the test project.
// Must appear after all top-level statements (including local functions) —
// otherwise CS8803.
public partial class Program { }
