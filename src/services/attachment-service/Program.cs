using Microsoft.Azure.Cosmos;
using System.Text.Json;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using AttachmentService;
using AttachmentService.Repositories;
using AttachmentService.Services;
using CloudHealthOffice.DocumentStore;

var builder = WebApplication.CreateBuilder(args);

// Azure AD Authentication (Multi-tenant)
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
    
    // Scope-based policies
    options.AddPolicy("RequireAttachmentUpload", policy =>
        policy.RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "Attachments.Upload", "Attachments.ReadWrite"));
    
    options.AddPolicy("RequireAttachmentDownload", policy =>
        policy.RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "Attachments.Download", "Attachments.ReadWrite"));
    
    // Role-based policies
    options.AddPolicy("AttachmentManager", policy =>
        policy.RequireRole("AttachmentManager", "Administrator"));
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Attachment Service API",
        Version = "v1",
        Description = "Clinical attachment management (275) for Cloud Health Office"
    });
    
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
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
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// Configure Cosmos DB with System.Text.Json serialization
var cosmosOptions = new CosmosClientOptions
{
    Serializer = new CosmosSystemTextJsonSerializer(
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        })
};

builder.Services.AddSingleton(s =>
{
    var config = s.GetRequiredService<IConfiguration>();
    var endpoint = config["CosmosDb:Endpoint"] ?? throw new InvalidOperationException("CosmosDb:Endpoint not configured");
    var key = config["CosmosDb:Key"] ?? throw new InvalidOperationException("CosmosDb:Key not configured");
    return new CosmosClient(endpoint, key, cosmosOptions);
});

// Configure Azure Blob Storage + IDocumentStore
builder.Services.AddSingleton(s =>
{
    var config = s.GetRequiredService<IConfiguration>();
    var connectionString = config["BlobStorage:ConnectionString"] ?? throw new InvalidOperationException("BlobStorage:ConnectionString not configured");
    return new Azure.Storage.Blobs.BlobServiceClient(connectionString);
});
builder.Services.AddSingleton<IDocumentStore, AzureBlobDocumentStore>();

builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
builder.Services.AddSingleton<AcknowledgmentGeneratorService>();
builder.Services.AddScoped<IAcknowledgmentService, AcknowledgmentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Health check endpoint (no auth required)
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "attachment-service" }))
   .AllowAnonymous();

app.Run();
