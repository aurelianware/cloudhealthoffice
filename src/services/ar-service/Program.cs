using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using ArService.Middleware;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
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
        Title = "AR Service API",
        Version = "v1",
        Description = "Accounts Receivable module — GL accounts, balances, cash posting, " +
                     "adjustments, and batch posting rules. Financial backbone that Premium Billing " +
                     "posts into and that FFS/Capitation payment engines draw GL entries from."
    });
});

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

    builder.Services.AddScoped<IGlAccountRepository, MongoGlAccountRepository>();
    builder.Services.AddScoped<IArBalanceRepository, MongoArBalanceRepository>();
    builder.Services.AddScoped<ICashPostingRepository, MongoCashPostingRepository>();
    builder.Services.AddScoped<IArAdjustmentRepository, MongoArAdjustmentRepository>();
    builder.Services.AddScoped<IArBatchRuleRepository, MongoArBatchRuleRepository>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    throw new InvalidOperationException("MongoDb:ConnectionString must be configured for ar-service");
}

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "AR Service API v1");
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

public partial class Program { }
