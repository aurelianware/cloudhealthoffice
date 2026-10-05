using System.Text.Json;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using AuthorizationService;
using AuthorizationService.Consumers;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Configuration;
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
// from that token. Reads need authorizations:read, writes
// authorizations:write; review decisions (approve/deny/pend, status
// transitions) additionally need authorizations:decide on the action.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "authorizations:read";
    auth.DefaultWritePermission = "authorizations:write";
});

// Add services to the container
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Authorization Service API",
        Version = "v1",
        Description = "Prior authorization management for Cloud Health Office. " +
                     "Handles 278 prior auth requests/responses, validates authorizations before claim submission."
    });
    
    // Add JWT Bearer authentication to Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your CHO access token.",
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
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Database Configuration (Cosmos DB or MongoDB)
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    // Use MongoDB
    builder.Services.AddScoped<IAuthorizationRepository, AuthorizationRepositoryMongo>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    // Use Cosmos DB (Default)
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"];
        var key = configuration["CosmosDb:Key"];
        
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(jsonOptions)
        };
        
        return new CosmosClient(endpoint, key, options);
    });

    builder.Services.AddScoped<IAuthorizationRepository, AuthorizationRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// ── Authorization backend seam (Replace / Augment) ──────────────────────────
// The controller/PAS workflow resolves an IAuthorizationBackend by operating
// mode rather than choosing a vendor type. Replace (default) = CHO-native
// (ChoAuthorizationBackend over the repository above). Augment = external core
// (QnxtAuthorizationBackend stub) — never a silent fallback to CHO.
builder.Services.Configure<AuthorizationService.Backends.AuthorizationBackendOptions>(
    builder.Configuration.GetSection(AuthorizationService.Backends.AuthorizationBackendOptions.SectionName));

// Benefit drug/service exclusion (CMS-0057-F PAS-08) — enforced by the CHO
// Replace-mode backend. The exclusion catalog is configuration-driven and
// tenant-scoped (no hard-coded codes); an empty catalog excludes nothing.
builder.Services.Configure<AuthorizationService.Services.BenefitExclusion.BenefitExclusionOptions>(
    builder.Configuration.GetSection(
        AuthorizationService.Services.BenefitExclusion.BenefitExclusionOptions.SectionName));
builder.Services.AddScoped<AuthorizationService.Services.BenefitExclusion.IBenefitExclusionCatalog,
    AuthorizationService.Services.BenefitExclusion.ConfiguredBenefitExclusionCatalog>();
builder.Services.AddScoped<AuthorizationService.Services.BenefitExclusion.IDrugExclusionEvaluator,
    AuthorizationService.Services.BenefitExclusion.DrugExclusionEvaluator>();
builder.Services.AddScoped<AuthorizationService.Services.BenefitExclusion.IAuthorizationExclusionService,
    AuthorizationService.Services.BenefitExclusion.AuthorizationExclusionService>();

builder.Services.AddScoped<AuthorizationService.Backends.IAuthorizationBackend,
    AuthorizationService.Backends.ChoAuthorizationBackend>();
builder.Services.AddScoped<AuthorizationService.Backends.IAuthorizationBackend,
    AuthorizationService.Backends.QnxtAuthorizationBackend>();
builder.Services.AddScoped<AuthorizationService.Backends.IAuthorizationBackendSelector,
    AuthorizationService.Backends.AuthorizationBackendSelector>();

// HTTP context accessor (repositories read the token tenant from HttpContext.Items)
builder.Services.AddHttpContextAccessor();

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

// Kafka consumer for RFAI docs received events
var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (!string.IsNullOrEmpty(kafkaBootstrap))
{
    builder.Services.AddHostedService<RfaiDocsReceivedConsumer>();
}

// SLA deadline watchdog (runs every 15 minutes)
builder.Services.AddHostedService<SlaWatchdogService>();

// ── CDex additional-information requests (PAS-07) ─────────────────────────────
// An A4 (pended — additional information required) decision that names what it
// needs raises a durable request in rfai-service, which owns that record. This
// service keeps only the handle. Registered unconditionally: the gateway degrades
// to "not recorded" when rfai-service is unreachable, and the retry is idempotent.
builder.Services.AddScoped<AuthorizationService.Services.Rfai.IRfaiRequestGateway,
    AuthorizationService.Services.Rfai.HttpRfaiRequestGateway>();
builder.Services.AddScoped<AuthorizationService.Services.Rfai.IPendedAuthorizationRfaiCoordinator,
    AuthorizationService.Services.Rfai.PendedAuthorizationRfaiCoordinator>();
builder.Services.AddHttpClient(
    AuthorizationService.Services.Rfai.HttpRfaiRequestGateway.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:RfaiServiceUrl"]
            ?? "http://rfai-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ── Prior-authorization data retention (PAT-03) ───────────────────────────────
// The policy is a pure, testable rule; the worker only discovers records it
// applies to. Disabled by default — a destructive sweep opts in per deployment.
builder.Services.Configure<AuthorizationService.Services.Retention.PriorAuthorizationRetentionOptions>(
    builder.Configuration.GetSection(
        AuthorizationService.Services.Retention.PriorAuthorizationRetentionOptions.SectionName));
builder.Services.AddScoped<AuthorizationService.Services.Retention.IPriorAuthorizationRetentionPolicy,
    AuthorizationService.Services.Retention.PriorAuthorizationRetentionPolicy>();
builder.Services.AddHostedService<AuthorizationService.Services.Retention.PriorAuthorizationRetentionWorker>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Authorization Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();


// Authentication, tenant from the token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
