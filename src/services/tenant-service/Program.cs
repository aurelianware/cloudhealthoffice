using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using TenantService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Tenant Management Service",
        Version = "v1",
        Description = "Multi-tenant SaaS tenant management for Cloud Health Office"
    });
});

// Cosmos DB
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:Endpoint"] ?? throw new InvalidOperationException("CosmosDb:Endpoint not configured");
    var key = configuration["CosmosDb:Key"] ?? throw new InvalidOperationException("CosmosDb:Key not configured");
    return new CosmosClient(endpoint, key);
});

// Repositories and services
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ITenantService, TenantManagementService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<ISftpProvisioningService, SftpProvisioningService>();

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
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

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();

// Health check endpoints
app.MapChoHealthChecks();

app.Run();
