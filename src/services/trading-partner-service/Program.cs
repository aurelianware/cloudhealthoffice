using CloudHealthOffice.TradingPartnerService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { 
        Title = "Trading Partner Service API", 
        Version = "v1",
        Description = "Manages trading partner configurations, SFTP paths, and X12 settings for multi-tenant EDI processing"
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
// Reads need trading-partners:read (ClaimsSupervisor, Finance; service tokens satisfy it).
// No trading-partners:write permission exists, so writes need settings:manage.
// The payment-service NPI lookup also admits payments:read (see the controller).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "trading-partners:read";
    auth.DefaultWritePermission = "settings:manage";
});

// Database — MongoDB is the only trading-partner store (ADR 006); the client
// is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Trading Partner Service requires a database. Configure MongoDb:ConnectionString.");
}

if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Trading Partner Service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

// Repository and services
builder.Services.AddScoped<ITradingPartnerRepository, TradingPartnerRepositoryMongo>();
builder.Services.AddScoped<PathResolver>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

// Health checks (MongoDB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// Health checks before auth so they're accessible without a token
app.MapChoHealthChecks();

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();
