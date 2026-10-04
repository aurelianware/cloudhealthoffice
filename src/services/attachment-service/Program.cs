using Microsoft.Azure.Cosmos;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using AttachmentService;
using AttachmentService.Repositories;
using AttachmentService.Services;
using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come
// from that token. Attachments are PHI, so nothing here is anonymous.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "attachments:read";
    auth.DefaultWritePermission = "attachments:write";
});

// Add services to the container.
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Attachment Service API",
        Version = "v1",
        Description = "Clinical attachment management (275) for Cloud Health Office"
    });
    
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });
    
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// Configure Cosmos DB with System.Text.Json serialization
var cosmosOptions = new CosmosClientOptions
{
    Serializer = new CosmosSystemTextJsonSerializer(
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        })
};

// Database provider selection. MongoDB is the default so the service stays cloud-agnostic;
// Cosmos DB's native SDK is opt-in via Database:Provider=CosmosDb.
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.CosmosDb)
{
    builder.Services.AddSingleton(s =>
    {
        var config = s.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"] ?? throw new InvalidOperationException("CosmosDb:Endpoint not configured");
        var key = config["CosmosDb:Key"] ?? throw new InvalidOperationException("CosmosDb:Key not configured");
        return new CosmosClient(endpoint, key, cosmosOptions);
    });
}

// Configure Azure Blob Storage + IDocumentStore
builder.Services.AddSingleton(s =>
{
    var config = s.GetRequiredService<IConfiguration>();
    var connectionString = config["BlobStorage:ConnectionString"] ?? throw new InvalidOperationException("BlobStorage:ConnectionString not configured");
    return new Azure.Storage.Blobs.BlobServiceClient(connectionString);
});
builder.Services.AddSingleton<IDocumentStore, AzureBlobDocumentStore>();

if (databaseProvider == ChoDatabaseProvider.CosmosDb)
{
    builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
}
else
{
    builder.Services.AddScoped<IAttachmentRepository, AttachmentRepositoryMongo>();
}

// Trading partners are trading-partner-service's data: read through its API,
// never from its TradingPartners collection (see HttpTradingPartnerLookup).
builder.Services.AddHttpClient(HttpTradingPartnerLookup.ClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:TradingPartnerService"]
        ?? HttpTradingPartnerLookup.DefaultBaseUrl).TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ITradingPartnerLookup, HttpTradingPartnerLookup>();
builder.Services.AddSingleton<AcknowledgmentGeneratorService>();
builder.Services.AddScoped<IAcknowledgmentService, AcknowledgmentService>();

// Health checks (MongoDB or Cosmos DB)
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

// Token authentication, tenant from the token, then permission policies.
app.UseChoAuthentication();

app.MapControllers();

// Health check endpoints (no auth required — handled by middleware before routing)
app.MapChoHealthChecks();

app.Run();
