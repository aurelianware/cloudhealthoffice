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

// QR signing + card generation
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddSingleton<IIdCardGenerator, IdCardGenerator>();
builder.Services.AddScoped<ITemplateResolver, TemplateResolver>();

// Adapters — registered as both interface and concrete type so the QNXT
// augment adapter can inject the Cho adapter directly.
builder.Services.AddScoped<ChoIdCardAdapter>();
builder.Services.AddScoped<IIdCardAdapter>(sp => sp.GetRequiredService<ChoIdCardAdapter>());
builder.Services.AddScoped<IIdCardAdapter, QnxtIdCardAdapter>();
builder.Services.AddScoped<IIdCardAdapter, FulfillmentVendorAdapter>();
builder.Services.AddScoped<IdCardAdapterFactory>();

// QNXT mirror queue — Service Bus in production, in-memory otherwise.
var sbConn = builder.Configuration["IdCard:QnxtMirror:ServiceBusConnectionString"];
var queueName = builder.Configuration["IdCard:QnxtMirror:QueueName"] ?? "qnxt-idcard-requests";
if (!string.IsNullOrEmpty(sbConn))
{
    builder.Services.AddSingleton<IQnxtMirrorQueue>(_ => new ServiceBusQnxtMirrorQueue(sbConn, queueName));
}
else
{
    builder.Services.AddSingleton<IQnxtMirrorQueue, InMemoryQnxtMirrorQueue>();
}

builder.Services.AddHostedService<QnxtMirrorReconciliationJob>();

builder.Services.AddScoped<IIdCardOrchestrator, IdCardOrchestrator>();

// Provider JWT for the /scan endpoint. When no authority configured, fall
// back to a permissive policy so dev/test environments can still exercise
// the endpoint without a real IdP.
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
else
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

// Rate limiter for /scan: partition the same policy on three dimensions —
// per-tenant, per-provider, per-cardId — so any one of them blowing past
// the threshold trips the limiter without starving the others.
var scanCfg = builder.Configuration.GetSection("IdCard:ScanRateLimit");
var perTenant = scanCfg.GetValue("PerTenantPerMinute", 600);
var perProvider = scanCfg.GetValue("PerProviderPerMinute", 120);
var perCard = scanCfg.GetValue("PerCardPerMinute", 30);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("card-scan", httpContext =>
    {
        var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "unknown";
        var providerId = httpContext.User.FindFirst("provider_id")?.Value
            ?? httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        var cardId = httpContext.Items["RateLimit:CardId"]?.ToString() ?? "pre-verify";
        var key = $"t:{tenantId}|p:{providerId}|c:{cardId}";
        var limit = Math.Min(Math.Min(perTenant, perProvider), perCard);
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

var app = builder.Build();

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
