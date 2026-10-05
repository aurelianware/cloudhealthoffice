using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;
using EncounterService;
using EncounterService.Repositories;
using EncounterService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
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
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Encounter Service API",
        Version = "v1",
        Description = "Healthcare encounter submission lifecycle for Cloud Health Office. " +
                     "Handles encounter submission, batch dispatch, 837 generation/download, " +
                     "payer acknowledgment tracking, and correction/resubmission workflows."
    });
    c.UseInlineDefinitionsForEnums();
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "encounters:read";
    auth.DefaultWritePermission = "encounters:write";
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IEncounterRepository, EncounterRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["CosmosDb:Endpoint"];
        var key = config["CosmosDb:Key"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");
        }

        var serializerOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(serializerOptions)
        };
        return new CosmosClient(endpoint, key, options);
    });

    builder.Services.AddScoped<IEncounterRepository, EncounterRepository>();
}

// 837 EDI generator
builder.Services.AddScoped<IEncounter837Service, Encounter837Service>();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Encounter Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();


// Authentication, then tenant from the token, then authorization.
app.UseChoAuthentication();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
