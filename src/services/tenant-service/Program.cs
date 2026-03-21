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
    var connectionString = configuration["CosmosDb:ConnectionString"];
    if (!string.IsNullOrEmpty(connectionString))
    {
        return new CosmosClient(connectionString);
    }
    var endpoint = configuration["CosmosDb:Endpoint"] ?? throw new InvalidOperationException("CosmosDb:Endpoint or CosmosDb:ConnectionString must be configured");
    var key = configuration["CosmosDb:Key"] ?? throw new InvalidOperationException("CosmosDb:Key not configured");
    return new CosmosClient(endpoint, key);
});

// Repositories and services
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ITenantUserRepository, TenantUserRepository>();
builder.Services.AddScoped<ITenantRoleRepository, TenantRoleRepository>();
builder.Services.AddScoped<ITenantService, TenantManagementService>();
builder.Services.AddScoped<ITenantUserService, TenantUserManagementService>();
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

// Auto-provision Cosmos DB database and containers on startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var cosmosClient = scope.ServiceProvider.GetRequiredService<CosmosClient>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    var databaseName = config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";

    try
    {
        var dbResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName);
        var database = dbResponse.Database;
        logger.LogInformation("Cosmos DB database '{DatabaseName}' ensured", databaseName);

        var containers = new[]
        {
            (Name: config["CosmosDb:TenantContainerName"] ?? "Tenants", PartitionKey: "/id"),
            (Name: config["CosmosDb:UserContainerName"] ?? "TenantUsers", PartitionKey: "/id"),
            (Name: config["CosmosDb:RoleContainerName"] ?? "TenantRoles", PartitionKey: "/id"),
        };

        foreach (var (name, partitionKey) in containers)
        {
            await database.CreateContainerIfNotExistsAsync(name, partitionKey);
            logger.LogInformation("Cosmos DB container '{ContainerName}' ensured", name);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to auto-provision Cosmos DB containers. They may need to be created manually.");
    }
}

// Seed the bootstrap TenantAdmin so the first user can log into the portal.
// Other tenants self-serve via /signup; their admins manage users at /settings/users.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var userRepo = scope.ServiceProvider.GetRequiredService<ITenantUserRepository>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var seedTenantId = config["SeedAdmin:TenantId"] ?? "32177734-051b-4fdc-9568-cc35530191b1";
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
            else
            {
                logger.LogDebug("Admin TenantUser {Email} already exists for tenant {TenantId}", seedEmail, seedTenantId);
            }
        }
        else
        {
            logger.LogDebug("SeedAdmin:Email not configured, skipping bootstrap seed");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed admin TenantUser. User will get TenantAdmin fallback role in portal.");
    }
}

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

// Seed standard RBAC roles on startup
using (var scope = app.Services.CreateScope())
{
    try
    {
        var roleRepository = scope.ServiceProvider.GetRequiredService<ITenantRoleRepository>();
        await roleRepository.SeedStandardRolesAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Failed to seed standard roles on startup. Roles will be seeded on first API call to POST /api/v1/roles/seed");
    }
}

app.Run();
