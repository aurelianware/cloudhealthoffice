using Microsoft.Azure.Cosmos;
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

// Cosmos DB
var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"] 
    ?? throw new InvalidOperationException("Cosmos DB connection string not configured");

builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    };
    return new CosmosClient(cosmosConnectionString, options);
});

// HTTP Client for service calls
builder.Services.AddHttpClient<IEligibilityService, EligibilityService>();

// Repository and Service
builder.Services.AddScoped<IEligibilityRepository, EligibilityRepository>();
builder.Services.AddScoped<IEligibilityService, EligibilityService>();

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
