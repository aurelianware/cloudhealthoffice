using Microsoft.Azure.Cosmos;
using SponsorService.Middleware;
using SponsorService.Repositories;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
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
        return wrapper.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
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

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

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
app.MapHealthChecks("/health");

app.Run();
