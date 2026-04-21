using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using AuthorizationService;
using AuthorizationService.Consumers;
using AuthorizationService.Middleware;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Azure AD Authentication (Multi-tenant organizational accounts)
// Frontend passes bearer token, services validate independently
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
    },
    options => { builder.Configuration.Bind("AzureAd", options); });

// Authorization policies (scope-based for single app registration)
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuthenticatedUser", policy =>
        policy.RequireAuthenticatedUser());
    
    // Scope-based policies (from single app registration)
    options.AddPolicy("RequireAuthorizationReadWrite", policy =>
        policy.RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "Authorization.ReadWrite"));
    
    options.AddPolicy("RequireAuthorizationRead", policy =>
        policy.RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "Authorization.Read", "Authorization.ReadWrite"));
    
    // Role-based policies (app roles from single app registration)
    options.AddPolicy("PriorAuthManager", policy =>
        policy.RequireRole("PriorAuthManager", "Administrator"));
});

// Add services to the container
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Authorization Service API",
        Version = "v1",
        Description = "Prior authorization management for Cloud Health Office. " +
                     "Handles 278 prior auth requests/responses, validates authorizations before claim submission."
    });
    
    // Add JWT Bearer authentication to Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token from Azure AD.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });
    
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Database Configuration (Cosmos DB or MongoDB)
if (!string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    // Use MongoDB
    builder.Services.AddSingleton<IMongoClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        return new MongoClient(configuration["MongoDb:ConnectionString"]);
    });

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var wrapper = sp.GetRequiredService<IMongoClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        return wrapper.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
    });

    builder.Services.AddScoped<IAuthorizationRepository, AuthorizationRepositoryMongo>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    // Use Cosmos DB (Default)
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"];
        var key = configuration["CosmosDb:Key"];
        
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        
        var options = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(jsonOptions)
        };
        
        return new CosmosClient(endpoint, key, options);
    });

    builder.Services.AddScoped<IAuthorizationRepository, AuthorizationRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

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

// Kafka consumer for RFAI docs received events
var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (!string.IsNullOrEmpty(kafkaBootstrap))
{
    builder.Services.AddHostedService<RfaiDocsReceivedConsumer>();
}

// SLA deadline watchdog (runs every 15 minutes)
builder.Services.AddHostedService<SlaWatchdogService>();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Authorization Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication MUST come before authorization
app.UseAuthentication();
app.UseAuthorization();

// Multi-tenant middleware (extract TenantId from validated JWT claims)
app.UseTenantMiddleware();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
