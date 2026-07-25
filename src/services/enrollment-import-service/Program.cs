using EnrollmentImportService;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Add services
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Cosmos DB
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["CosmosDb:Endpoint"] ?? "https://localhost:8081";
    var key = config["CosmosDb:Key"] ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    
    var options = new CosmosClientOptions
    {
        Serializer = new CosmosSystemTextJsonSerializer()
    };
    
    return new CosmosClient(endpoint, key, options);
});

// Repositories and services
builder.Services.AddScoped<IEnrollmentRepository, EnrollmentRepository>();
builder.Services.AddScoped<IEnrollmentTransactionRepository, EnrollmentTransactionRepository>();
builder.Services.AddScoped<IEnrollmentEventRepository, EnrollmentEventRepository>();
builder.Services.AddScoped<IEnrollmentEventPublisher, EnrollmentEventPublisher>();
builder.Services.AddSingleton<IEnrollmentValidator, EnrollmentValidator>();
builder.Services.AddScoped<IEnrollmentImportService, EnrollmentImportService.Services.EnrollmentImportService>();
builder.Services.AddSingleton<IEnrollment834EdiParser, Enrollment834EdiParser>();

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

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseAuthorization();
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
