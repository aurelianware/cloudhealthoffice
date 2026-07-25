using Microsoft.Azure.Cosmos;
using SponsorService.Middleware;
using SponsorService.Repositories;
using MongoDB.Driver;
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
        Title = "Cloud Health Office - Sponsor Service API", 
        Version = "v1",
        Description = "Manages employer/group sponsor data populated by X12 834 Enrollment transactions"
    });
});

// Database Configuration (Cosmos DB or MongoDB)
if (!string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    // Use MongoDB
    builder.Services.AddSingleton<IMongoClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        return new MongoClient(configuration["MongoDb:ConnectionString"]);
    });

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var wrapper = sp.GetRequiredService<IMongoClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        return wrapper.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "cloudhealthoffice");
    });

    builder.Services.AddScoped<ISponsorRepository, SponsorRepositoryMongo>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    // Use Cosmos DB (Default)
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

    builder.Services.AddScoped<ISponsorRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new SponsorRepository(cosmosClient, databaseName);
    });
    Console.WriteLine("Using Cosmos DB repository");
}

// CORS (configure as needed)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Health checks (MongoDB or Cosmos DB)
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

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware pipeline
app.UseCors();
app.UseTenantContext();  // Extract tenant from JWT or header
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();
