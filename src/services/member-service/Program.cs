using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
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

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
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
    builder.Services.AddSingleton<IMemberAlertRepository, MemberAlertRepositoryMongo>();
    builder.Services.AddSingleton<IMemberNoteRepository, MemberNoteRepositoryMongo>();

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

    builder.Services.AddScoped<IMemberAlertRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberAlertRepository(cosmosClient, databaseName);
    });

    builder.Services.AddScoped<IMemberNoteRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberNoteRepository(cosmosClient, databaseName);
    });

    Console.WriteLine($"Using Cosmos DB database provider (events container: {eventsContainerName})");
}

// ── Member Service internals ─────────────────────────────────────────
builder.Services.AddScoped<IMemberEventPublisher, CosmosMemberEventPublisher>();
builder.Services.AddSingleton<IFhirPatientProjector, FhirPatientProjector>();
builder.Services.AddSingleton<IFhirFlagProjector, FhirFlagProjector>();
builder.Services.AddScoped<IFamilyRelationshipService, FamilyRelationshipService>();
builder.Services.AddScoped<IRelationshipShim, RelationshipShim>();
builder.Services.AddScoped<IMemberAlertGuard, MemberAlertGuard>();

// Identifier encryption. Rotation-aware KV-backed encryptor when either
// the new MemberEncryption section is configured OR the legacy
// Member:IdentifierEncryption:KeySecretName is set — the legacy config
// is bridged onto MemberEncryptionOptions so a service booting with no
// new config keeps the pre-A.7.3 behaviour: v1 current, v1 accepted, and
// the legacy secret name used for both rotation (KeySecretPrefix) and
// 0x01 decrypt.
var encryptionKeySecretName = builder.Configuration["Member:IdentifierEncryption:KeySecretName"];
var memberEncryptionSection = builder.Configuration.GetSection(MemberEncryptionOptions.SectionName);
if (memberEncryptionSection.Exists() || !string.IsNullOrWhiteSpace(encryptionKeySecretName))
{
    var options = ResolveMemberEncryptionOptions(memberEncryptionSection, encryptionKeySecretName);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IIdentifierEncryptor>(sp =>
        new KeyVaultIdentifierEncryptor(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ISecretProvider>(),
            sp.GetRequiredService<ILogger<KeyVaultIdentifierEncryptor>>(),
            options));

    Console.WriteLine(
        $"[member-service] IIdentifierEncryptor = KeyVault (rotating) — current: {options.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", options.AcceptedKeyVersions)}]; " +
        $"legacy 0x01 secret: {options.LegacyKeySecretName ?? "<none>"}");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        // Fail loudly in non-dev rather than silently passing PII through plaintext.
        throw new InvalidOperationException(
            "MemberEncryption (or legacy Member:IdentifierEncryption:KeySecretName) must be configured in non-development environments.");
    }
    builder.Services.AddSingleton<IIdentifierEncryptor, NoOpIdentifierEncryptor>();
    Console.WriteLine("[dev] IIdentifierEncryptor = NoOp (PII stored plaintext). Configure MemberEncryption to enable.");
}

static MemberEncryptionOptions ResolveMemberEncryptionOptions(
    IConfigurationSection section, string? legacyKeySecretName)
{
    var bound = section.Exists() ? section.Get<MemberEncryptionOptions>() : null;
    if (bound is not null)
    {
        // If the new section is partially configured but LegacyKeySecretName
        // is absent, fall back to the pre-A.7.3 config key so 0x01 envelopes
        // keep decrypting.
        var legacy = string.IsNullOrWhiteSpace(bound.LegacyKeySecretName)
            ? legacyKeySecretName ?? bound.KeySecretPrefix
            : bound.LegacyKeySecretName;
        return bound with { LegacyKeySecretName = legacy };
    }

    // Pure legacy path — no MemberEncryption block, only the old single-name
    // key. Emit 0x01 envelopes against that exact secret so new writes have
    // the same shape as pre-A.7.3 writes and don't reference a versioned
    // secret name that was never published. Decrypt still supports both 0x01
    // (via the same legacy key) and 0x02 (should a stray one appear). When
    // the operator adds an explicit MemberEncryption section they opt into
    // 0x02 emission.
    return new MemberEncryptionOptions
    {
        KeySecretPrefix = legacyKeySecretName!,
        CurrentKeyVersion = "v1",
        AcceptedKeyVersions = new[] { "v1" },
        LegacyKeySecretName = legacyKeySecretName,
        EmitLegacyEnvelope = true
    };
}

// Identifier fingerprinting (HMAC-SHA256 keyed by a DISTINCT KV secret from
// the encryption key — rotated independently). Used to dedupe PII
// identifiers without comparing ciphertexts (which differ by AES-GCM nonce
// even for identical plaintext). Dual-read during rotation windows via
// IIdentifierFingerprinter.FingerprintCandidatesAsync.
var fingerprintKeySecretName =
    builder.Configuration["Encryption:IdentifierFingerprintKeySecret"]
    ?? builder.Configuration["Member:IdentifierFingerprint:KeySecretName"];
var memberFingerprintingSection = builder.Configuration.GetSection(MemberFingerprintingOptions.SectionName);
if (memberFingerprintingSection.Exists() || !string.IsNullOrWhiteSpace(fingerprintKeySecretName))
{
    var fpOptions = ResolveMemberFingerprintingOptions(memberFingerprintingSection, fingerprintKeySecretName);

    builder.Services.AddSingleton(fpOptions);
    builder.Services.AddSingleton<IIdentifierFingerprinter>(sp =>
        new HmacSha256IdentifierFingerprinter(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ISecretProvider>(),
            sp.GetRequiredService<ILogger<HmacSha256IdentifierFingerprinter>>(),
            fpOptions));

    Console.WriteLine(
        $"[member-service] IIdentifierFingerprinter = HmacSha256 (rotating) — current: {fpOptions.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", fpOptions.AcceptedKeyVersions)}]; " +
        $"legacy secret: {fpOptions.LegacyKeySecretName ?? "<none>"}");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "MemberFingerprinting (or legacy Encryption:IdentifierFingerprintKeySecret) must be configured in non-development environments.");
    }
    builder.Services.AddSingleton<IIdentifierFingerprinter, NoOpIdentifierFingerprinter>();
    Console.WriteLine("[dev] IIdentifierFingerprinter = NoOp (plain SHA-256). Configure MemberFingerprinting to enable.");
}

static MemberFingerprintingOptions ResolveMemberFingerprintingOptions(
    IConfigurationSection section, string? legacyKeySecretName)
{
    var bound = section.Exists() ? section.Get<MemberFingerprintingOptions>() : null;
    if (bound is not null)
    {
        var legacy = string.IsNullOrWhiteSpace(bound.LegacyKeySecretName)
            ? legacyKeySecretName ?? bound.KeySecretPrefix
            : bound.LegacyKeySecretName;
        return bound with { LegacyKeySecretName = legacy };
    }

    // Pure legacy path: treat the old single-name secret as the implicit v1
    // entry. FingerprintAsync resolves v1 via the legacy name fallback in
    // HmacSha256IdentifierFingerprinter, so existing rows keep deduping
    // without requiring the operator to publish a prefix-versioned secret.
    return new MemberFingerprintingOptions
    {
        KeySecretPrefix = legacyKeySecretName!,
        CurrentKeyVersion = "v1",
        AcceptedKeyVersions = new[] { "v1" },
        LegacyKeySecretName = legacyKeySecretName
    };
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
