using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CorrespondenceService.Repositories;
using CorrespondenceService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; tenant and user come from it. Letters
// are PHI: reading needs correspondence:read plus the read permission of a
// subject; requesting needs correspondence:write; templates need
// correspondence:templates and approval correspondence:approve
// (CorrespondenceController).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = CorrespondenceService.Controllers.CorrespondenceController.Read;
    auth.DefaultWritePermission = CorrespondenceService.Controllers.CorrespondenceController.Write;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Correspondence Service API",
        Version = "v1",
        Description = "Letter types, versioned templates and rendered letters, stored write-once in the document catalog",
    });
});

// Rendered letters live in the document catalog (document-service): storage,
// retention, legal hold and the access audit are the catalog's. This service
// keeps letter types, templates and letter records in MongoDB.
builder.Services.AddHttpClient(HttpLetterDocumentStore.ClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:DocumentService"]
        ?? HttpLetterDocumentStore.DefaultBaseUrl).TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<ILetterDocumentStore, HttpLetterDocumentStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LetterComposer>();

if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Correspondence Service requires a database. Configure MongoDb:ConnectionString.");
}
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Correspondence Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}
builder.Services.AddScoped<ICorrespondenceRepository, CorrespondenceRepositoryMongo>();

// No CORS: called server-to-server only (the portal is Blazor Server).

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
