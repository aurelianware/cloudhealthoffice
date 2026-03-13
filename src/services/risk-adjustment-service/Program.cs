using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using RiskAdjustmentService;
using RiskAdjustmentService.Middleware;
using RiskAdjustmentService.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Risk Adjustment Service API",
        Version = "v1",
        Description = "Healthcare risk adjustment scoring for Cloud Health Office. " +
                     "Provides per-member HCC risk scores, measurement-year data, " +
                     "and population risk analytics for Medicare Advantage, Medicaid, and ACA plans."
    });
});

// Database Configuration
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
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

    builder.Services.AddScoped<IRiskScoreRepository, RiskScoreRepositoryMongo>();
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

        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        };
        return new CosmosClient(endpoint, key, options);
    });

    builder.Services.AddScoped<IRiskScoreRepository, RiskScoreRepository>();
}

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Health checks
builder.Services.AddHealthChecks();

// CORS (for development)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Risk Adjustment Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();

// Multi-tenant middleware
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
