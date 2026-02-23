using Microsoft.Azure.Cosmos;
using BenefitPlanService.Middleware;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using MongoDB.Driver;

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
    Console.WriteLine("Using Cosmos DB repository");
}

// Add business logic services
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanServiceImpl>();

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
