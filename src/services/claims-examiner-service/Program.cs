using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Examiner;
using ClaimsExaminerService.Services.Kafka;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / env var fallback)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Shared infrastructure: health checks, CORS, Swagger, tenant middleware,
// observability. Same opinionated pipeline as claims-service.
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Examiner Service";
    options.ServiceDescription =
        "AI-powered advisory examiner for pended claims. Subscribes to claims.pended.v1, " +
        "calls Anthropic Claude with NCCI bundling context, and writes an advisory " +
        "recommendation back to claims-service for human work-queue review. Pend-resolution " +
        "only — never auto-applies, never bypasses human review.";
});

// Anthropic client (typed HttpClient + bound options)
var anthropicOptions = builder.Configuration.GetSection("Anthropic").Get<AnthropicOptions>()
    ?? new AnthropicOptions();
builder.Services.AddSingleton(anthropicOptions);
builder.Services.AddTransient<AnthropicAuthHandler>();
builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>()
    .AddHttpMessageHandler<AnthropicAuthHandler>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

// Claims-service client (typed HttpClient)
builder.Services.AddHttpClient<IClaimsServiceClient, ClaimsServiceClient>(client =>
{
    var baseUrl = builder.Configuration["Services:ClaimsService"]
        ?? "http://claims-service:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// Examiner pipeline
builder.Services.AddSingleton<IExaminerPromptBuilder, ExaminerPromptBuilder>();
builder.Services.AddScoped<IExaminerOrchestrator, ExaminerOrchestrator>();

// Provider RFAI history enrichment. v1 ships with the no-op default — when an
// aggregate endpoint lands on rfai-service, swap this for an HttpClient-backed
// implementation. The orchestrator already passes RFAI history through to the
// prompt builder; the data path is the only piece missing.
builder.Services.AddSingleton<IProviderRfaiHistoryClient, NoOpProviderRfaiHistoryClient>();

// Kafka consumer (background service). Always registered: when Kafka isn't
// configured the consumer logs a warning and exits cleanly, so dev environments
// without Kafka still come up.
builder.Services.AddHostedService<ClaimPendedConsumer>();

var app = builder.Build();

// Shared middleware pipeline
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();

// Expose Program for WebApplicationFactory in integration tests
public partial class Program { }
