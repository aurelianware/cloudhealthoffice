using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the uploader come from that
// token. Member documents (ID cards, letters, EOBs, uploads) are PHI: reads and
// downloads need members:read, uploads and finalize need members:write.
// Placing or releasing a legal hold (or uploading under one) needs
// records:legal-hold instead. This service makes no outbound CHO calls.
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

// Document bytes go to the configured store (Azure Blob, Google Cloud Storage
// or S3; see AddChoDocumentStore). This service keeps the records in MongoDB.
builder.Services.AddChoDocumentStore(builder.Environment.EnvironmentName);
builder.Services.AddSingleton<IRetentionPolicyService, RetentionPolicyService>();
builder.Services.AddSingleton(sp => MemberDocumentUploadPolicy.FromConfiguration(
    sp.GetRequiredService<IConfiguration>()));

// Database — MongoDB is the only member-document store (ADR 006); the client
// is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Member Document Service requires a database. Configure MongoDb:ConnectionString.");
}

if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Member Document Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<IMemberDocumentRepository, MemberDocumentRepositoryMongo>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

// Fail at startup, not on the first upload, when the document store is misconfigured.
app.Services.GetRequiredService<IDocumentStore>();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
