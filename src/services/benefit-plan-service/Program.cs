using Microsoft.Azure.Cosmos;
using BenefitPlanService.Middleware;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using MongoDB.Driver;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.BenefitEngine.Configuration;
using CloudHealthOffice.BenefitEngine.Persistence;
using StackExchange.Redis;
using CloudHealthOffice.FeeScheduleEngine.Configuration;
var builder = WebApplication.CreateBuilder(args);

// Configure Database (Cosmos DB or MongoDB)
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
        return wrapper.GetDatabase(configuration["MongoDb:DatabaseName"]);
    });

    builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepositoryMongo>();
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();
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
        return new CosmosClient(endpoint, key);
    });

    builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepository>();
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryCosmos>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Add business logic services
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanServiceImpl>();

// Required by RedisAccumulatorService — reads TenantId set by TenantMiddleware
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IBenefitEngineTenantContext, HttpContextTenantContext>();

// Typed HttpClient for claims-service (used by ClaimsServiceAccumulatorSource on cache miss)
builder.Services.AddHttpClient<IClaimsAccumulatorSource, ClaimsServiceAccumulatorSource>(client =>
{
    var url = builder.Configuration["Services:ClaimsServiceUrl"]
              ?? throw new InvalidOperationException(
                     "Services:ClaimsServiceUrl is required when using the Redis accumulator service. " +
                     "Add it to appsettings.json or as an environment variable.");
    client.BaseAddress = new Uri(url);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Redis-backed (recommended for production)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is required.")));

builder.Services.AddBenefitEngine()
    .UseChoBenefitPlanProvider()
    .UseRedisAccumulatorService();

builder.Services.AddFeeScheduleEngine()
    .UseRepositoriesFromConfiguration(builder.Configuration);


// Audit trail: write accumulator history to MongoDB/Cosmos alongside the Redis hot cache
builder.Services.AddScoped<IAccumulatorAuditWriter, MongoAccumulatorAuditWriter>();


// Add controllers
builder.Services.AddControllers();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
builder.Services.AddHealthChecks();

// Add CORS
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
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Add tenant middleware
app.UseMiddleware<TenantMiddleware>();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
