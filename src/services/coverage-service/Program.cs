using Microsoft.Azure.Cosmos;
using CoverageService.Middleware;
using CoverageService.Repositories;
using CoverageService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Coverage Service API", 
        Version = "v1",
        Description = "Links Member → Sponsor → Benefit Plan. Populated by X12 834 HD/COB segments. Critical for 270/271 eligibility."
    });
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    // MongoDB Registration
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(sp => 
    {
        return new MongoDB.Driver.MongoClient(mongoConnectionString);
    });
    
    builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var databaseName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(databaseName);
    });

    builder.Services.AddScoped<ICoverageRepository, CoverageRepositoryMongo>();
    builder.Services.AddScoped<IPcpAssignmentRepository, PcpAssignmentRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"] 
            ?? throw new InvalidOperationException("CosmosDb:Endpoint configuration missing");
        var key = configuration["CosmosDb:Key"] 
            ?? throw new InvalidOperationException("CosmosDb:Key configuration missing");
        
        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        });
    });

    builder.Services.AddScoped<ICoverageRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new CoverageRepository(cosmosClient, databaseName);
    });

    builder.Services.AddScoped<IPcpAssignmentRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new PcpAssignmentRepository(cosmosClient, databaseName);
    });
}

// PCP assignment + provider client + care team projector
builder.Services.Configure<ProviderServiceOptions>(opts =>
{
    opts.BaseUrl = builder.Configuration["Downstream:ProviderService:BaseUrl"];
});
builder.Services.AddHttpClient<IProviderServiceClient, HttpProviderServiceClient>((sp, client) =>
{
    var baseUrl = builder.Configuration["Downstream:ProviderService:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl)) client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddHttpClient<IPanelCounter, HttpPanelCounter>((sp, client) =>
{
    // Panel counter reads the capitation-style /by-pcp roster. Prefer the
    // capitation-service URL when set; fall back to the coverage-service URL
    // (since coverage itself exposes /by-pcp). If neither is configured, leave
    // BaseAddress null — HttpPanelCounter short-circuits to 0 in that case so
    // panel-limit checks degrade gracefully rather than gating every assignment.
    var baseUrl = builder.Configuration["Downstream:CapitationService:BaseUrl"]
                 ?? builder.Configuration["Downstream:CoverageService:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }
});
builder.Services.AddScoped<IPcpAssignmentService, PcpAssignmentService>();
builder.Services.AddSingleton<ICareTeamProjector, CareTeamProjector>();
builder.Services.AddScoped<PcpPanelReconciliationJob>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseTenantContext();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();
