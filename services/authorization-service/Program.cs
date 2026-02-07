using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using AuthorizationService;
using AuthorizationService.Middleware;
using AuthorizationService.Repositories;

var builder = WebApplication.CreateBuilder(args);

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

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuthenticatedUser", policy =>
        policy.RequireAuthenticatedUser());
    
    options.AddPolicy("PriorAuthManager", policy =>
        policy.RequireRole("PriorAuthManager", "Administrator"));
});

// Add services to the container
builder.Services.AddControllers();
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

// Repositories
builder.Services.AddScoped<IAuthorizationRepository, AuthorizationRepository>();

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Health checks
builder.Services.AddHealthChecks();

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Authorization Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Authentication MUST come before authorization
app.UseAuthentication();
app.UseAuthorization();

// Multi-tenant middleware (extract TenantId from validated JWT claims)
app.UseTenantMiddleware();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
