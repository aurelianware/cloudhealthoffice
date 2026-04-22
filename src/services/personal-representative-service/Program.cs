using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using PersonalRepresentativeService.HostedServices;
using PersonalRepresentativeService.Middleware;
using PersonalRepresentativeService.Repositories;
using PersonalRepresentativeService.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — same bootstrap shape as all
// other services in the platform.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Personal Representative Service API",
        Version = "v1",
        Description = "Records Personal Representative delegation (§164.502(g)) with symmetric-pair associations, field-level encryption, and an append-only audit trail."
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

    builder.Services.AddSingleton<IPersonalRepEventRepository, PersonalRepEventRepositoryMongo>();
    builder.Services.AddSingleton<IPersonalRepEventSink>(sp =>
        (IPersonalRepEventSink)sp.GetRequiredService<IPersonalRepEventRepository>());
    builder.Services.AddSingleton<IPersonalRepRepository, PersonalRepRepositoryMongo>();

    builder.Services.AddHostedService<PersonalRepIndexInitializer>();

    Console.WriteLine("[personal-representative-service] Using MongoDB database provider");
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

    builder.Services.AddScoped<IPersonalRepEventRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new PersonalRepEventRepository(cosmosClient, databaseName);
    });
    builder.Services.AddScoped<IPersonalRepEventSink>(sp =>
        (IPersonalRepEventSink)sp.GetRequiredService<IPersonalRepEventRepository>());
    builder.Services.AddScoped<IPersonalRepRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var sink = sp.GetRequiredService<IPersonalRepEventSink>();
        var logger = sp.GetRequiredService<ILogger<PersonalRepRepository>>();
        return new PersonalRepRepository(cosmosClient, databaseName, sink, logger);
    });

    Console.WriteLine("[personal-representative-service] Using Cosmos DB database provider");
}

// ── Personal Rep body encryption ────────────────────────────────────
// Non-dev startup guard: no PersonalRepEncryption section = startup error.
// Only IsDevelopment() falls back to the no-op encryptor.
var encryptionSection = builder.Configuration.GetSection(PersonalRepEncryptionOptions.SectionName);
if (encryptionSection.Exists())
{
    var options = encryptionSection.Get<PersonalRepEncryptionOptions>() ?? new PersonalRepEncryptionOptions();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IPersonalRepFieldEncryptor>(sp =>
        new PersonalRepFieldEncryptor(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ILogger<PersonalRepFieldEncryptor>>(),
            options));

    Console.WriteLine(
        $"[personal-representative-service] IPersonalRepFieldEncryptor = KeyVault (rotating) — current: {options.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", options.AcceptedKeyVersions)}]");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "PersonalRepEncryption must be configured in non-development environments. " +
            "Publish the personal-rep-body-encryption-key-v1 secret and set PersonalRepEncryption:KeySecretPrefix / CurrentKeyVersion.");
    }
    builder.Services.AddSingleton(new PersonalRepEncryptionOptions());
    builder.Services.AddSingleton<IPersonalRepFieldEncryptor, NoOpPersonalRepFieldEncryptor>();
    Console.WriteLine("[dev] IPersonalRepFieldEncryptor = NoOp (personal rep body fields stored plaintext). Configure PersonalRepEncryption to enable.");
}

// ── Kafka producer (personal rep status events) ──────────────────────
builder.Services.AddSingleton<PersonalRepEventPublisher>();
builder.Services.AddSingleton<IPersonalRepEventPublisher>(sp => sp.GetRequiredService<PersonalRepEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersonalRepEventPublisher>());

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

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
})
.AddCheck<PersonalRepEncryptionKeyHealthCheck>(
    "personal-rep-encryption-key",
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
