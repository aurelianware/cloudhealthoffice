using Microsoft.Azure.Cosmos;
using SponsorService.Middleware;
using SponsorService.Repositories;

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
builder.Services.AddScoped<ISponsorRepository>(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
    return new SponsorRepository(cosmosClient, databaseName);
});

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
