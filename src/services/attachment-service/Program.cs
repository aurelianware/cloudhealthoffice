using System.Text.Json;
using Microsoft.OpenApi.Models;
using AttachmentService;
using AttachmentService.Repositories;
using AttachmentService.Services;
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

// Database — MongoDB is the only attachment store (ADR 006, ADR 017);
// AddChoDatabase registers the client. A host with no database configured
// refuses to start, in every environment.
if (builder.Services.AddChoDatabase(builder.Configuration) is null)
{
    throw new InvalidOperationException(
        "Attachment Service requires a database. Configure MongoDb:ConnectionString.");
}

// Attachment files live in the document catalog (document-service), which
// owns their storage, links and access audit.
builder.Services.AddHttpClient(HttpDocumentCatalog.ClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:DocumentService"]
        ?? HttpDocumentCatalog.DefaultBaseUrl).TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<IDocumentCatalog, HttpDocumentCatalog>();

builder.Services.AddScoped<IAttachmentRepository, AttachmentRepositoryMongo>();

// Trading partners are trading-partner-service's data: read through its API,
// never from its TradingPartners collection (see HttpTradingPartnerLookup).
builder.Services.AddHttpClient(HttpTradingPartnerLookup.ClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:TradingPartnerService"]
        ?? HttpTradingPartnerLookup.DefaultBaseUrl).TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ITradingPartnerLookup, HttpTradingPartnerLookup>();

// An attachment is stored only against a claim, authorization or appeal that
// exists for the caller's tenant, read from the service that owns it.
foreach (var (name, key, fallback) in new[]
{
    (HttpAttachmentParentLookup.ClaimsClientName, "Services:ClaimsService", HttpAttachmentParentLookup.ClaimsDefaultBaseUrl),
    (HttpAttachmentParentLookup.AuthorizationClientName, "Services:AuthorizationService", HttpAttachmentParentLookup.AuthorizationDefaultBaseUrl),
    (HttpAttachmentParentLookup.AppealsClientName, "Services:AppealsService", HttpAttachmentParentLookup.AppealsDefaultBaseUrl),
})
{
    builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri((builder.Configuration[key] ?? fallback).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);
    });
}
builder.Services.AddScoped<IAttachmentParentLookup, HttpAttachmentParentLookup>();
builder.Services.Configure<AttachmentUploadOptions>(builder.Configuration.GetSection(AttachmentUploadOptions.SectionName));
builder.Services.AddSingleton<AcknowledgmentGeneratorService>();
builder.Services.AddScoped<IAcknowledgmentService, AcknowledgmentService>();

// Health checks (MongoDB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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
