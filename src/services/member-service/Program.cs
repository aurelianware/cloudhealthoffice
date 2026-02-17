using Microsoft.Azure.Cosmos;
using MemberService.Middleware;
using MemberService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Member Service API", 
        Version = "v1",
        Description = "Manages health plan member data (subscribers and dependents) populated by X12 834 Enrollment transactions"
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

    builder.Services.AddScoped<IMemberRepository, MemberRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    // Cosmos DB client (singleton)
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

    // Register repositories
    builder.Services.AddScoped<IMemberRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new MemberRepository(cosmosClient, databaseName);
    });
    Console.WriteLine("Using Cosmos DB database provider");
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseTenantContext();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
