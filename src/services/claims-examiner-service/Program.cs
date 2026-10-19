using CloudHealthOffice.Infrastructure.Http;
using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Events;
using ClaimsExaminerService.Services.Examiner;
using ClaimsExaminerService.Services.Kafka;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

// `--check-anthropic` runs one real Messages API call with this service's
// configuration and exits 0/1 (deploys run it inside the pod after rollout).
var checkAnthropic = args.Contains(AnthropicConnectivityCheck.CommandLineFlag);
args = args.Where(a => a != AnthropicConnectivityCheck.CommandLineFlag).ToArray();

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / env var fallback)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Shared infrastructure: health checks, CORS, Swagger, observability. Same opinionated pipeline as claims-service.
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Examiner Service";
    options.ServiceDescription =
        "AI-powered advisory examiner for pended claims. Subscribes to claims.pended.v1, " +
        "calls Anthropic Claude with NCCI bundling context, and writes an advisory " +
        "recommendation back to claims-service for human work-queue review. Pend-resolution " +
        "only — never auto-applies, never bypasses human review.";
});

// ── Authentication ──────────────────────────────────────────────────
// Every inbound caller presents a CHO token; the tenant and the acting user come
// from that token (UseChoInfrastructure runs UseChoAuthentication). The service
// exposes no controllers today, so the defaults only guard future endpoints.
// Outbound: factory-built clients get ChoOutboundTokenHandler. The Kafka consumer
// has no caller, so calls to claims-service carry a service token minted for the
// tenant the ClaimPendedEvent names (sent as X-Tenant-ID by ClaimsServiceClient).
// The handler never attaches CHO tokens to external hosts such as Anthropic.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "claims:read";
    auth.DefaultWritePermission = "claims:work";
});

// Anthropic client (typed HttpClient + bound options)
var anthropicOptions = builder.Configuration.GetSection("Anthropic").Get<AnthropicOptions>()
    ?? new AnthropicOptions();
builder.Services.AddSingleton(anthropicOptions);
builder.Services.AddTransient<AnthropicAuthHandler>();
// The model timeout is set here, not in the client's constructor: the shared
// resilience reads it as the attempt timeout, sizes the total timeout to cover
// every attempt and its back-off, and raises HttpClient.Timeout past that total.
// (A constructor-set HttpClient.Timeout would cap all attempts together.)
builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>(
        client => client.Timeout = TimeSpan.FromSeconds(anthropicOptions.TimeoutSeconds))
    .AddHttpMessageHandler<AnthropicAuthHandler>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    // 429 (rate limited) and 529 (overloaded) are retried with backoff, honouring
    // Retry-After. The Messages call is a POST but has no side effect, so it may be
    // repeated. One attempt may take the configured model timeout.
    .ConfigureChoResilience(o =>
    {
        o.RetryNonIdempotent = true;
        o.AdditionalTransientStatusCodes = [429, 529];
        o.MaxRetryAttempts = 3;
        o.RetryBaseDelay = TimeSpan.FromSeconds(2);
        o.CircuitMinimumThroughput = 5;
        o.CircuitBreakDuration = TimeSpan.FromSeconds(30);
    });
builder.Services.AddHttpClient<AnthropicConnectivityCheck>(
        client => client.Timeout = TimeSpan.FromSeconds(Math.Max(anthropicOptions.TimeoutSeconds, 5)))
    .AddHttpMessageHandler<AnthropicAuthHandler>();

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
// The shared producer (idempotent, acks=all): dead-letters claims.pended.v1
// messages that failed every attempt.
builder.Services.AddSingleton<CloudHealthOffice.Events.Kafka.IChoEventProducer>(sp =>
{
    var kafka = sp.GetRequiredService<IConfiguration>().GetSection("Kafka");
    return new CloudHealthOffice.Events.Kafka.ChoKafkaProducer(new CloudHealthOffice.Events.Kafka.ChoKafkaProducerSettings
    {
        BootstrapServers = kafka["BootstrapServers"],
        ClientId = kafka["ClientId"] ?? "claims-examiner-service",
        SaslUsername = kafka["SaslUsername"],
        SaslPassword = kafka["SaslPassword"],
    });
});
builder.Services.Configure<ClaimPendedConsumerOptions>(builder.Configuration.GetSection(ClaimPendedConsumerOptions.SectionName));
builder.Services.AddSingleton<ClaimPendedMessageProcessor>();
builder.Services.AddHostedService<ClaimPendedConsumer>();

// Capability 5.9 — Service Bus producer for the AI examination completion
// event. Same backend-resolution semantics as claims-service: Auto resolves
// to ServiceBus when Messaging:ServiceBusConnectionString is configured,
// InMemory otherwise. Singleton because Service Bus client is thread-safe
// and per-call construction would defeat connection pooling.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IAiExaminationEventPublisher, ServiceBusAiExaminationEventPublisher>();

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

if (checkAnthropic)
{
    var check = app.Services.GetRequiredService<AnthropicConnectivityCheck>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var result = await check.RunAsync(app.Configuration["Anthropic:ApiKey"], cts.Token);
    Console.WriteLine(result.Message);
    return result.Ok ? 0 : 1;
}

app.UseChoObservability();

// Shared middleware pipeline (includes UseChoAuthentication: authentication,
// tenant from the token, authorization)
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();
return 0;

// Expose Program for WebApplicationFactory in integration tests
public partial class Program { }
