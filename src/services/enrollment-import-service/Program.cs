using EnrollmentImportService;
using EnrollmentImportService.Clients;
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
builder.Services.AddSingleton<IEnrollmentTransactionRepository, EnrollmentTransactionRepository>();
builder.Services.AddSingleton<IEnrollmentImportRunRepository, EnrollmentImportRunRepository>();
builder.Services.AddSingleton<IEnrollmentEventRepository, EnrollmentEventRepository>();
builder.Services.AddScoped<IEnrollmentEventPublisher, EnrollmentEventPublisher>();
builder.Services.AddSingleton<IEnrollmentValidator, EnrollmentValidator>();
builder.Services.AddScoped<IEnrollmentImportService, EnrollmentImportService.Services.EnrollmentImportService>();
builder.Services.AddSingleton<IEnrollment834EdiParser, Enrollment834EdiParser>();
builder.Services.AddSingleton<IPlanCodeGapReportService, PlanCodeGapReportService>();

builder.Services.AddHostedService<EnrollmentIndexInitializer>();

// member-service / sponsor-service clients — enrollment-import-service used
// to write Member/Sponsor documents directly into Mongo collections that
// collide with the ones those now-split-out services actually own (see
// IMemberServiceClient's doc comment). Delegating via HTTP instead.
//
// Fallback URLs are the k8s Service's actual port (80, not the container's
// 8080) — confirmed live: claims-service's own HttpMemberResolver has this
// exact ":8080" mistake in its code fallback too, silently masked in every
// environment by an explicit Services__MemberService=http://member-service
// env var override. Not repeating that here; see the deployment yaml for
// the belt-and-suspenders explicit env vars.
builder.Services.AddHttpClient(HttpMemberServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:MemberService"]
        ?? "http://member-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IMemberServiceClient, HttpMemberServiceClient>();

builder.Services.AddHttpClient(HttpSponsorServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:SponsorService"]
        ?? "http://sponsor-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ISponsorServiceClient, HttpSponsorServiceClient>();

// benefit-plan-service client — resolves a trading partner's 834 plan code
// (HD04) to this platform's canonical PlanId before Coverage is written.
builder.Services.AddHttpClient(HttpBenefitPlanServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanService"]
        ?? "http://benefit-plan-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IBenefitPlanServiceClient, HttpBenefitPlanServiceClient>();

// coverage-service client — Coverage used to be the one entity this service
// still wrote directly into a Mongo collection shared with coverage-service's
// own repository. Delegating via HTTP now that PlanId is actually resolved.
builder.Services.AddHttpClient(HttpCoverageServiceClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:CoverageService"]
        ?? "http://coverage-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ICoverageServiceClient, HttpCoverageServiceClient>();

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
