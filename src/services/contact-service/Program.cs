using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using ContactService.HostedServices;
using ContactService.Repositories;
using ContactService.Services.Claims;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — the same bootstrap shape as
// every other service in the platform.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Cloud Health Office - Contact Service API",
        Version = "v1",
        Description =
            "Call tracking: the record of a call, chat, email or walk-in, what it was about, " +
            "and what it touched. See docs/architecture/cho-service.md.",
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting rep come from
// that token only, never from a request body.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = ChoRolePermissions.ServiceRead;
    auth.DefaultWritePermission = ChoRolePermissions.ServiceContact;
});

// ── Database ─────────────────────────────────────────────────────────
// MongoDB is the only contact store (ADR 006, ADR 017). A host with no
// database configured refuses to start, in every environment, rather than
// going Ready and failing every request.
if (builder.Services.AddChoDatabase(builder.Configuration) is null)
{
    throw new InvalidOperationException(
        "Contact Service requires a database. Configure MongoDb:ConnectionString.");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ContactIndexes>();
builder.Services.AddSingleton<ContactReasonIndexes>();
builder.Services.AddScoped<IContactRepository, ContactRepositoryMongo>();
builder.Services.AddScoped<IContactReasonRepository, ContactReasonRepositoryMongo>();
builder.Services.AddHostedService<ContactIndexInitializer>();

// ── Reading a contact is itself a record read ────────────────────────
// A contact names the member and what was discussed, so who opened it is
// part of the HIPAA accounting, the same as a member or claim record page.
builder.Services.AddRecordAccessAudit();

// ── Notes on what was discussed ──────────────────────────────────────
// Linking a claim writes a contact note on it through claims-service, with
// the rep's own token (ChoOutboundTokenHandler), so the note is authored by
// the person who took the call. Without claims-service configured the link
// is still recorded on the contact; only the claim's own history misses it.
var claimsServiceUrl = builder.Configuration["Services:ClaimsServiceUrl"]
                    ?? builder.Configuration["Downstream:ClaimsService:BaseUrl"];
if (!string.IsNullOrWhiteSpace(claimsServiceUrl))
{
    builder.Services.AddHttpClient(HttpContactNoteGateway.HttpClientName, client =>
    {
        client.BaseAddress = new Uri(claimsServiceUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddScoped<IContactNoteGateway, HttpContactNoteGateway>();
}
else
{
    builder.Services.AddSingleton<IContactNoteGateway, NullContactNoteGateway>();
}

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

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

app.UseChoAuthentication();
app.MapControllers();
app.MapRecordAccessLog("api/contacts", "Contact");
app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
