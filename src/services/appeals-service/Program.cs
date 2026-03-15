using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using AppealsService.Middleware;
using AppealsService.Repositories;
using CloudHealthOffice.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Appeals Service API",
        Version = "v1",
        Description = "Claim appeals processing with 275 attachment support for Cloud Health Office. " +
                     "Handles appeal workflow, documentation submission, and decision tracking."
    });
});

// Cosmos DB client (singleton)
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

// Repositories
builder.Services.AddScoped<IAppealRepository, AppealRepository>();

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Appeals Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseHttpsRedirection();

// Multi-tenant middleware (extract TenantId from JWT or headers)
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
