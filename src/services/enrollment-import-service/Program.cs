using EnrollmentImportService;
using EnrollmentImportService.HostedServices;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Add services
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// MongoDB
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["MongoDb:ConnectionString"]
        ?? throw new InvalidOperationException("MongoDb:ConnectionString is required.");
    return new MongoClient(connectionString);
});
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var databaseName = sp.GetRequiredService<IConfiguration>()["MongoDb:DatabaseName"] ?? "cloudhealthoffice";
    return client.GetDatabase(databaseName);
});

// Repositories and services. Constructed without I/O side effects (index
// creation happens in EnrollmentIndexInitializer below), so these can be
// singletons rather than scoped — same pattern as member-service.
builder.Services.AddSingleton<IEnrollmentRepository, EnrollmentRepository>();
builder.Services.AddSingleton<IEnrollmentTransactionRepository, EnrollmentTransactionRepository>();
builder.Services.AddSingleton<IEnrollmentEventRepository, EnrollmentEventRepository>();
builder.Services.AddScoped<IEnrollmentEventPublisher, EnrollmentEventPublisher>();
builder.Services.AddSingleton<IEnrollmentValidator, EnrollmentValidator>();
builder.Services.AddScoped<IEnrollmentImportService, EnrollmentImportService.Services.EnrollmentImportService>();
builder.Services.AddSingleton<IEnrollment834EdiParser, Enrollment834EdiParser>();

builder.Services.AddHostedService<EnrollmentIndexInitializer>();

// Health checks
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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
