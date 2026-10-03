using Azure.Storage.Blobs;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the uploader come from that
// token. Member documents (ID cards, letters, EOBs, uploads) are PHI: reads and
// downloads need members:read, uploads, finalize and legal hold need
// members:write. This service makes no outbound CHO calls.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "members:read";
    auth.DefaultWritePermission = "members:write";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Member Document Service API",
        Version = "v1",
        Description = "Member document ingestion, retrieval, legal hold, retention tagging, and FHIR DocumentReference projection"
    });
});

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration["BlobStorage:ConnectionString"]
        ?? throw new InvalidOperationException("BlobStorage:ConnectionString configuration missing");
    return new BlobServiceClient(connectionString);
});

builder.Services.AddScoped<IMemberDocumentBlobService, MemberDocumentBlobService>();
builder.Services.AddSingleton<IRetentionPolicyService, RetentionPolicyService>();
builder.Services.AddSingleton(sp => MemberDocumentUploadPolicy.FromConfiguration(
    sp.GetRequiredService<IConfiguration>()));

var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IMemberDocumentRepository, MemberDocumentRepositoryMongo>();
}
else
{
    builder.Services.AddSingleton(sp =>
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

    builder.Services.AddScoped<IMemberDocumentRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberDocumentRepository(cosmosClient, databaseName, configuration);
    });
}

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

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors();
// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
