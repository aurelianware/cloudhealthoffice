using System.Text.Json;
using System.Threading.RateLimiting;
using IdCardService;
using IdCardService.Adapters;
using IdCardService.Middleware;
using IdCardService.Repositories;
using IdCardService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<TenantActionFilter>();
}).AddCloudHealthOfficeJsonOptions();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - ID Card Service API",
        Version = "v1",
        Description = "Digital ID card issuance, QR scan verification, and member card history"
    });
});

// Storage: Mongo if configured, else Cosmos if configured, else in-memory.
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(_ =>
        new MongoDB.Driver.MongoClient(mongoConnectionString));

    builder.Services.AddScoped(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var dbName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });

    builder.Services.AddScoped<IIdCardOrderRepository, MongoIdCardOrderRepository>();
    builder.Services.AddScoped<IIdCardRecordRepository, MongoIdCardRecordRepository>();
    builder.Services.AddScoped<IIdCardTemplateRepository, MongoIdCardTemplateRepository>();
    Console.WriteLine("idcard-service: using MongoDB storage");
}
else if (!string.IsNullOrEmpty(cosmosConnectionString))
{
    builder.Services.AddSingleton(_ =>
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var options = new CosmosClientOptions { Serializer = new CosmosSystemTextJsonSerializer(jsonOptions) };
        return new CosmosClient(cosmosConnectionString, options);
    });

    builder.Services.AddScoped<IIdCardOrderRepository, CosmosIdCardOrderRepository>();
    builder.Services.AddScoped<IIdCardRecordRepository, CosmosIdCardRecordRepository>();
    builder.Services.AddScoped<IIdCardTemplateRepository, CosmosIdCardTemplateRepository>();
    Console.WriteLine("idcard-service: using Cosmos DB storage");
}
else
{
    builder.Services.AddSingleton<IIdCardOrderRepository, InMemoryIdCardOrderRepository>();
    builder.Services.AddSingleton<IIdCardRecordRepository, InMemoryIdCardRecordRepository>();
    builder.Services.AddSingleton<IIdCardTemplateRepository, InMemoryIdCardTemplateRepository>();
    Console.WriteLine("idcard-service: using in-memory storage (dev only)");
}

builder.Services.AddHttpClient("IdCardDefault")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10));

// Upstream clients
builder.Services.AddSingleton<IMemberClient, MemberClient>();
builder.Services.AddSingleton<ICoverageClient, CoverageClient>();
builder.Services.AddSingleton<ISponsorClient, SponsorClient>();
builder.Services.AddSingleton<IBenefitPlanClient, BenefitPlanClient>();
builder.Services.AddSingleton<IMemberDocumentClient, MemberDocumentClient>();
builder.Services.AddSingleton<IEligibilityClient, EligibilityClient>();

// QR signing + card generation. RotatingKeyProvider is registered by
// AddSecretProvider above; QrCodeService consumes it directly so the
// per-service key cache stays in sync with IConfiguration reloads.
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();

// Startup log: visible in pod logs so ops can confirm the rotation window
// the service booted with. No secret material logged.
{
    var current = builder.Configuration["IdCard:CurrentKeyVersion"] ?? "v1";
    var accepted = builder.Configuration.GetSection("IdCard:AcceptedKeyVersions").Get<string[]>()
        ?? new[] { current };
    Console.WriteLine(
        $"[idcard-service] QrCodeService rotating keys — current: {current}; accepted: [{string.Join(", ", accepted)}]");
}
builder.Services.AddSingleton<IIdCardGenerator, IdCardGenerator>();
builder.Services.AddScoped<ITemplateResolver, TemplateResolver>();

// Adapters — registered as both interface and concrete type so the QNXT
// augment adapter can inject the Cho adapter directly.
builder.Services.AddScoped<ChoIdCardAdapter>();
builder.Services.AddScoped<IIdCardAdapter>(sp => sp.GetRequiredService<ChoIdCardAdapter>());
builder.Services.AddScoped<IIdCardAdapter, QnxtIdCardAdapter>();
builder.Services.AddScoped<IIdCardAdapter, FulfillmentVendorAdapter>();
builder.Services.AddScoped<IdCardAdapterFactory>();

// Shared messaging bus. Backend (ServiceBus / InMemory / Null) is resolved
// from Messaging:* config + environment by AddChoMessaging.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// QNXT mirror queue — when the feature flag is enabled we route through
// IMessageBus (Service Bus in prod, in-memory in dev). When disabled we
// keep the stand-alone InMemoryQnxtMirrorQueue because tests and the
// reconciliation job rely on its PeekEnqueued inspection method.
var qnxtMirrorEnabled = builder.Configuration.GetValue<bool>("IdCard:QnxtMirror:Enabled");
var qnxtQueueName = builder.Configuration["IdCard:QnxtMirror:QueueName"] ?? "qnxt-idcard-requests";
if (qnxtMirrorEnabled)
{
    builder.Services.AddSingleton<IQnxtMirrorQueue>(sp =>
        new ServiceBusQnxtMirrorQueue(sp.GetRequiredService<IMessageBus>(), qnxtQueueName));
}
else
{
    builder.Services.AddSingleton<IQnxtMirrorQueue, InMemoryQnxtMirrorQueue>();
}

builder.Services.AddHostedService<QnxtMirrorReconciliationJob>();

builder.Services.AddScoped<IIdCardOrchestrator, IdCardOrchestrator>();

// Provider JWT for the /scan endpoint. Production requires ProviderJwt:Authority —
// the permissive dev scheme is *only* wired when we're running in the
// Development environment. In every other environment, missing Authority
// is a startup failure so a misconfiguration can't silently disable auth.
var jwtAuthority = builder.Configuration["ProviderJwt:Authority"];
var jwtAudience = builder.Configuration["ProviderJwt:Audience"];
if (!string.IsNullOrEmpty(jwtAuthority))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = jwtAuthority;
            options.Audience = jwtAudience;
            options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("ProviderJwt:RequireHttpsMetadata", true);
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("ProviderJwt", p => p.RequireAuthenticatedUser());
    });
}
else if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication("ProviderJwt-Dev")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevProviderAuthHandler>(
            "ProviderJwt-Dev", _ => { });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("ProviderJwt", p =>
            p.RequireAuthenticatedUser().AddAuthenticationSchemes("ProviderJwt-Dev"));
    });
}
else
{
    throw new InvalidOperationException(
        "ProviderJwt:Authority is not configured. The permissive dev auth handler is only enabled in the Development environment; "
        + "configure ProviderJwt:Authority (and optionally Audience) for non-development deployments.");
}

// Rate limiter for /scan. The ASP.NET RateLimiter middleware runs before
// the MVC action, so we can't key on cardId (the QR payload lives in the
// POST body and parsing it pre-action would consume the stream). Phase 1
// partitions on (tenant, provider) with the stricter per-provider cap
// applied; per-card enforcement is a Phase-2 follow-up that will require
// either a dedicated pre-MVC middleware that buffers + parses the body
// or a route shape like /scan/{cardId}.
var scanCfg = builder.Configuration.GetSection("IdCard:ScanRateLimit");
var perTenant = scanCfg.GetValue("PerTenantPerMinute", 600);
var perProvider = scanCfg.GetValue("PerProviderPerMinute", 120);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        var resp = context.HttpContext.Response;
        resp.StatusCode = StatusCodes.Status429TooManyRequests;
        resp.ContentType = "application/json";
        var payload = new
        {
            code = IdCardService.Models.ScanErrorCodes.RateLimited,
            message = "Too many scan requests. Please retry shortly."
        };
        await JsonSerializer.SerializeAsync(resp.Body, payload, cancellationToken: ct);
    };
    options.AddPolicy("card-scan", httpContext =>
    {
        var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "unknown";
        var providerId = httpContext.User.FindFirst("provider_id")?.Value
            ?? httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        // Key the bucket on (tenant, provider) so the same provider hitting
        // multiple tenants gets independent buckets, and use the stricter
        // of the two configured caps as the per-bucket limit. An overall
        // per-tenant cap (summed across providers) is enforced via the
        // max-concurrent-providers guardrail in the service mesh.
        var key = $"t:{tenantId}|p:{providerId}";
        var limit = Math.Min(perTenant, perProvider);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
});

// Global-template check: surfaces missing-seed deployments as a readiness
// failure so rollouts catch it before a member ever hits the order endpoint.
builder.Services.AddHealthChecks()
    .AddCheck<GlobalTemplateHealthCheck>("idcard-global-template", tags: new[] { "ready" });

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ID Card Service API v1");
    });
}

app.UseCors("AllowAll");
app.UseIdCardTenantMiddleware();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
