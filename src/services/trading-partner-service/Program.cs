using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using CloudHealthOffice.TradingPartnerService.Services;
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
    c.SwaggerDoc("v1", new() { 
        Title = "Trading Partner Service API", 
        Version = "v1",
        Description = "Manages trading partner configurations, SFTP paths, and X12 settings for multi-tenant EDI processing"
    });
});

// Azure AD JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdministratorRole", policy =>
        policy.RequireRole("Administrator"));
});

// Cosmos DB Client (singleton)
builder.Services.AddSingleton(sp =>
{
    var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") 
        ?? builder.Configuration["CosmosDb:Endpoint"]
        ?? throw new InvalidOperationException("COSMOS_ENDPOINT not configured");
    
    var key = Environment.GetEnvironmentVariable("COSMOS_KEY") 
        ?? builder.Configuration["CosmosDb:Key"]
        ?? throw new InvalidOperationException("COSMOS_KEY not configured");

    return new CosmosClient(endpoint, key);
});

// Repository and services
builder.Services.AddScoped<ITradingPartnerRepository, TradingPartnerRepository>();
builder.Services.AddScoped<PathResolver>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:5000",
                "http://portal.cloudhealthoffice",
                "https://portal.cloudhealthoffice.com")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Health checks before auth so they're accessible without a token
app.MapChoHealthChecks();

app.UseAuthentication();
app.UseAuthorization();

// Multi-tenant middleware (extract TenantId from validated JWT claims)
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var tenantIdClaim = context.User.Claims.FirstOrDefault(c => c.Type == "tenant_id" || c.Type == "http://schemas.microsoft.com/identity/claims/tenantid");
        if (tenantIdClaim != null)
        {
            context.Items["TenantId"] = tenantIdClaim.Value;
        }
    }

    await next();
});

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();
