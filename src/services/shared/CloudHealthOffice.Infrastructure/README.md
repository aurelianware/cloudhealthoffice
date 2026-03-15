# CloudHealthOffice.Infrastructure

Shared infrastructure library that extracts common patterns duplicated across all Cloud Health Office microservices into a single reusable package.

## What's Included

| Component | File | Replaces |
|---|---|---|
| Tenant Middleware | `Middleware/TenantMiddleware.cs` | 17 duplicate TenantMiddleware.cs files |
| HttpContext Extensions | `Middleware/HttpContextExtensions.cs` | Inline `HttpContext.Items["TenantId"]` access |
| Exception Handling | `Middleware/ExceptionHandlingMiddleware.cs` | Ad-hoc try/catch in controllers |
| Standard Error Response | `Models/StandardErrorResponse.cs` | Inconsistent error shapes across services |
| Health Checks | `HealthChecks/HealthCheckExtensions.cs` | 22 inconsistent health check setups |
| MongoDB Connection Factory | `Data/MongoDbConnectionFactory.cs` | Manual `IMongoDatabase` registration with inline config |
| Cosmos Serializer | `Serialization/CosmosSystemTextJsonSerializer.cs` | 10 duplicate serializer files |
| Service Extensions | `Extensions/ServiceCollectionExtensions.cs` | Duplicated Program.cs boilerplate |

## Migration Guide

### Before (typical service Program.cs)

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using ClaimsService.Middleware;
using ClaimsService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Claims Service API",
        Version = "v1",
        Description = "Healthcare claims processing..."
    });
});

// Manual database setup
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(sp =>
        new MongoDB.Driver.MongoClient(mongoConnectionString));
    builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var dbName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });
    builder.Services.AddScoped<IClaimRepository, ClaimRepositoryMongo>();
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        };
        return new CosmosClient(config["CosmosDb:Endpoint"], config["CosmosDb:Key"], options);
    });
    builder.Services.AddScoped<IClaimRepository, ClaimRepository>();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseTenantMiddleware();  // Custom per-service middleware
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
```

### After (with CloudHealthOffice.Infrastructure)

```csharp
using CloudHealthOffice.Infrastructure.Extensions;
using ClaimsService.Repositories;

var builder = WebApplication.CreateBuilder(args);

// One call replaces: AddControllers, AddSwagger, AddCors, AddHealthChecks,
// AddHttpContextAccessor, MongoDB/Cosmos registration, TenantMiddleware options
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Service";
    options.ServiceDescription = "Healthcare claims processing for Cloud Health Office.";
    // options.TenantOptions.RequireTenantId = true;  // strict mode
});

// Service-specific registrations only
builder.Services.AddScoped<IClaimRepository, ClaimRepository>();

var app = builder.Build();

// IMPORTANT: If your service uses JWT/Azure AD authentication, register it
// BEFORE UseChoInfrastructure so HttpContext.User is populated for tenant
// claim extraction:
//   app.UseAuthentication();

// One call replaces: UseSwagger, UseHttpsRedirection, UseTenantMiddleware,
// UseCors, UseAuthorization, MapHealthChecks, ExceptionHandling
app.UseChoInfrastructure(builder.Configuration);

app.MapControllers();
app.Run();
```

### Step-by-step migration

1. **Add project reference** to your service's `.csproj`:
   ```xml
   <ItemGroup>
     <ProjectReference Include="../shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj" />
   </ItemGroup>
   ```

2. **Replace Program.cs boilerplate** with `AddChoInfrastructure()` + `UseChoInfrastructure()` (see above).

3. **Delete local duplicates** — search your service for these types and remove them:
   - `TenantMiddleware` class (typically in a `Middleware/` folder, but some services place it elsewhere)
   - `CosmosSystemTextJsonSerializer` class (found in `Middleware/`, project root, or other locations depending on the service)
   - Any `TenantMiddlewareExtensions` (`UseTenantMiddleware()` / `UseTenantContext()`)

4. **If your service uses authentication**, ensure `app.UseAuthentication()` is called
   **before** `app.UseChoInfrastructure()`. The shared tenant middleware reads
   `HttpContext.User` claims, which requires the authentication middleware to have run first.

5. **Update tenant ID access** in controllers/repositories:
   ```csharp
   // Before
   var tenantId = HttpContext.Items["TenantId"]?.ToString();

   // After
   using CloudHealthOffice.Infrastructure.Middleware;
   var tenantId = HttpContext.GetTenantId();
   ```

6. **Use MongoDbConnectionFactory** for tenant-scoped databases (optional):
   ```csharp
   // Enable in appsettings.json:
   "MongoDb": {
     "UseTenantScoping": true
   }
   ```

## Configuration

### ChoInfrastructureOptions

| Property | Default | Description |
|---|---|---|
| `ServiceName` | `null` | Display name for Swagger UI |
| `ServiceDescription` | `null` | Description for Swagger UI |
| `TenantOptions` | see below | Tenant middleware configuration |
| `ConfigureCors` | `null` | Custom CORS configuration. When null, uses permissive AllowAll policy |
| `CorsPolicyName` | `"AllowAll"` | CORS policy name used in the pipeline. Must match a registered policy |

#### Custom CORS example

```csharp
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Service";
    options.CorsPolicyName = "Production";
    options.ConfigureCors = cors =>
    {
        cors.AddPolicy("Production", policy =>
        {
            policy.WithOrigins("https://app.cloudhealthoffice.com")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    };
});
```

### TenantMiddlewareOptions

| Property | Default | Description |
|---|---|---|
| `RequireTenantId` | `false` | `true` = 401 if missing; `false` = use default |
| `DefaultTenantId` | `"default-tenant"` | Fallback when not required |
| `PassthroughPaths` | `/health`, `/ready`, `/live`, `/swagger` | Paths that skip tenant resolution |

### Tenant resolution order

1. JWT claim: `tenant_id` or `extension_TenantId` or `GroupSid`
2. HTTP header: `X-Tenant-ID`
3. HTTP header: `X-Dev-Tenant-ID`
4. Default or 401 (based on `RequireTenantId`)

### Health check endpoints

| Endpoint | Checks | Tags |
|---|---|---|
| `/health` | All registered checks | — |
| `/live` | Self check only | `live` |
| `/ready` | MongoDB, Redis, HTTP dependencies | `ready` |

### Authentication

`UseChoInfrastructure()` does **not** call `UseAuthentication()`. Services that rely on JWT or Azure AD must register authentication themselves:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => { /* ... */ });

var app = builder.Build();
app.UseAuthentication();           // MUST come before UseChoInfrastructure
app.UseChoInfrastructure(config);
app.MapControllers();
```

This is intentional — not all services require authentication, and authentication configuration varies across services (Azure AD B2C, custom JWT, etc.).
