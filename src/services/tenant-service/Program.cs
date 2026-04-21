using Microsoft.OpenApi.Models;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TenantService.Services;
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
        Title = "Tenant Management Service",
        Version = "v1",
        Description = "Multi-tenant SaaS tenant management for Cloud Health Office"
    });
});

// MongoDB — camelCase convention to match stored field names
var camelCasePack = new ConventionPack { new CamelCaseElementNameConvention() };
ConventionRegistry.Register("CamelCase", camelCasePack, _ => true);

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration["MongoDb:ConnectionString"]
        ?? configuration["CosmosDb:ConnectionString"]
        ?? throw new InvalidOperationException("MongoDb:ConnectionString must be configured");
    return new MongoClient(connectionString);
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var mongoClient = sp.GetRequiredService<IMongoClient>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databaseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
    return mongoClient.GetDatabase(databaseName);
});

// Repositories and services
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ITenantUserRepository, TenantUserRepository>();
builder.Services.AddScoped<ITenantRoleRepository, TenantRoleRepository>();
builder.Services.AddScoped<ITenantService, TenantManagementService>();
builder.Services.AddScoped<ITenantUserService, TenantUserManagementService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<ISftpProvisioningService, SftpProvisioningService>();

// Health checks
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"]
        ?? builder.Configuration["CosmosDb:ConnectionString"];
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

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

// Seed admin TenantUser and standard roles on startup.
// MongoDB collections are auto-created on first write — no provisioning needed.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // Seed bootstrap admin
    try
    {
        var userRepo = scope.ServiceProvider.GetRequiredService<ITenantUserRepository>();
        var seedTenantId = config["SeedAdmin:TenantId"] ?? "aurelianware";
        var seedEmail = config["SeedAdmin:Email"] ?? "";

        if (!string.IsNullOrEmpty(seedEmail))
        {
            var existing = await userRepo.GetByEmailAsync(seedTenantId, seedEmail);
            if (existing == null)
            {
                var adminUser = new TenantService.Models.TenantUser
                {
                    TenantId = seedTenantId,
                    Email = seedEmail,
                    EmailNormalized = seedEmail.ToLowerInvariant(),
                    DisplayName = config["SeedAdmin:DisplayName"] ?? seedEmail.Split('@')[0],
                    FirstName = config["SeedAdmin:FirstName"] ?? seedEmail.Split('@')[0],
                    LastName = config["SeedAdmin:LastName"] ?? "",
                    Roles = new List<string> { "TenantAdmin" },
                    Department = "Administration",
                    Status = "Active"
                };
                await userRepo.CreateAsync(adminUser);
                logger.LogInformation("Seeded admin TenantUser {Email} for tenant {TenantId}", seedEmail, seedTenantId);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed admin TenantUser. User will get TenantAdmin fallback role in portal.");
    }

    // Seed standard RBAC roles
    try
    {
        var roleRepository = scope.ServiceProvider.GetRequiredService<ITenantRoleRepository>();
        await roleRepository.SeedStandardRolesAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed standard roles on startup.");
    }
}

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

// Health check endpoints
app.MapChoHealthChecks();

app.Run();
