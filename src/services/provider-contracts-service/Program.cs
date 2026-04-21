using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using ProviderContractsService.Middleware;
using ProviderContractsService.Repositories;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Provider Contracts Service API",
        Version = "v1",
        Description = "Master provider contract management. Holds the legal agreement between " +
                     "the health plan and a provider/group. Payment-method-specific configuration " +
                     "(capitation rates, FFS fee schedules) are child records referencing contracts by ContractId."
    });
});

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Database Configuration — MongoDB
if (!string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
        new MongoClient(sp.GetRequiredService<IConfiguration>()["MongoDb:ConnectionString"]));

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var dbName = sp.GetRequiredService<IConfiguration>()["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });

    builder.Services.AddScoped<IProviderContractRepository, MongoProviderContractRepository>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    throw new InvalidOperationException("MongoDb:ConnectionString must be configured for provider-contracts-service");
}

// Health checks
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Provider Contracts Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
