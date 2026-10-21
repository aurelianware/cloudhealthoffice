using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Repositories;
using DocumentService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the actor come from it.
// GETs need documents:read and writes documents:write, and every document
// read also needs the read permission of the subject it is opened from
// (SubjectAccess). Legal hold needs records:legal-hold; the access log
// audit:read. This service makes no outbound CHO calls.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = ChoRolePermissions.DocumentsRead;
    auth.DefaultWritePermission = ChoRolePermissions.DocumentsWrite;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Document Service API",
        Version = "v1",
        Description = "The document catalog: documents linked to the claims, authorizations, appeals, members and other subjects they support, with context-based access, sensitivity tags, an access audit, retention and legal hold."
    });
});

// Document bytes go to the configured store (Azure Blob, Google Cloud Storage
// or S3); the catalog lives in MongoDB.
builder.Services.AddChoDocumentStore(builder.Environment.EnvironmentName);
builder.Services.AddSingleton<IDocumentScanner, NoDocumentScanner>();
builder.Services.AddSingleton<IRetentionPolicyService, RetentionPolicyService>();
builder.Services.AddSingleton(sp => DocumentUploadPolicy.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.Configure<DocumentServiceOptions>(builder.Configuration.GetSection(DocumentServiceOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Document Service requires a database. Configure MongoDb:ConnectionString.");
}

if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Document Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddSingleton<IDocumentRepository, DocumentRepositoryMongo>();
builder.Services.AddSingleton<IDocumentAccessAudit, DocumentAccessAuditMongo>();

// No CORS: called server-to-server (the portal is Blazor Server).

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
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
