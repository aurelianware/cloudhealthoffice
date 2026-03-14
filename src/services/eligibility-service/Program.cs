using System.Text.Json;
using Microsoft.Azure.Cosmos;
using EligibilityService;
using EligibilityService.Adapters;
using EligibilityService.Middleware;
using EligibilityService.Repositories;
using EligibilityService.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers(options =>
{
    options.Filters.Add<TenantActionFilter>();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - Eligibility Service API",
        Version = "v1",
        Description = "Real-time eligibility verification (270/271 EDI transactions)"
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

    builder.Services.AddScoped<IEligibilityRepository, EligibilityRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    // Cosmos DB
    var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"] 
        ?? throw new InvalidOperationException("Cosmos DB connection string not configured");

    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(jsonOptions)
        };
        return new CosmosClient(cosmosConnectionString, options);
    });

    builder.Services.AddScoped<IEligibilityRepository, EligibilityRepository>();
}

// HTTP Client for service calls (shared by adapters, factory, and eligibility service)
builder.Services.AddHttpClient("EligibilityDefault")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

// Eligibility adapters — each tenant can be configured to use a different platform
builder.Services.AddSingleton<IEligibilityAdapter>(sp =>
    new ChoEligibilityAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("EligibilityDefault"),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<ChoEligibilityAdapter>>()));
builder.Services.AddSingleton<IEligibilityAdapter>(sp =>
    new AvailityEligibilityAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("EligibilityDefault"),
        sp.GetRequiredService<ILogger<AvailityEligibilityAdapter>>()));
builder.Services.AddSingleton<IEligibilityAdapter>(sp =>
    new ChangeHealthcareEligibilityAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("EligibilityDefault"),
        sp.GetRequiredService<ILogger<ChangeHealthcareEligibilityAdapter>>()));
builder.Services.AddSingleton<EligibilityAdapterFactory>(sp =>
    new EligibilityAdapterFactory(
        sp.GetRequiredService<IEnumerable<IEligibilityAdapter>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("EligibilityDefault"),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<EligibilityAdapterFactory>>()));

builder.Services.AddHttpClient<IEligibilityService, EligibilityServiceImpl>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

builder.Services.AddScoped<IEligibilityService, EligibilityServiceImpl>();

// 270/271 EDI services
builder.Services.AddScoped<IEdi270Parser, Edi270Parser>();
builder.Services.AddScoped<IEdi271Generator, Edi271Generator>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Eligibility Service API v1");
    });
}

app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
